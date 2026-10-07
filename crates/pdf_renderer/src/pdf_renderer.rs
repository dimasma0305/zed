use std::io::{Read, Write};

use anyhow::{Context as _, Result, ensure};
use hayro::{
    PixmapSettings, RenderCache, RenderSettings, hayro_interpret::InterpreterSettings,
    hayro_syntax::Pdf, vello_cpu::color::palette::css::WHITE,
};

pub const WORKER_ARGUMENT: &str = "--pdf-render-worker";
pub const MAX_FILE_BYTES: usize = 128 * 1024 * 1024;
pub const MAX_PIXELS: usize = 16 * 1024 * 1024;
pub const MAX_OUTPUT_BYTES: usize = MAX_PIXELS * 4 + 28;
const MAX_PAGES: usize = 10_000;
const MAX_DIMENSION: f32 = 8192.0;
const MAX_PAGE_DIMENSION: f32 = 1_000_000.0;
const REQUEST_MAGIC: &[u8; 8] = b"ZPDF0001";
const RESPONSE_MAGIC: &[u8; 8] = b"ZIMG0001";

pub struct RenderedPage {
    pub page_count: u32,
    pub page_width: f32,
    pub page_height: f32,
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

pub fn read_document(input: impl Read) -> Result<Vec<u8>> {
    let mut bytes = Vec::new();
    input
        .take((MAX_FILE_BYTES + 1) as u64)
        .read_to_end(&mut bytes)?;
    ensure!(
        bytes.len() <= MAX_FILE_BYTES,
        "PDF exceeds the 128 MiB file limit"
    );
    Ok(bytes)
}

pub fn request_header(page_index: u32, scale: f32, length: usize) -> Result<Vec<u8>> {
    ensure!(
        length <= MAX_FILE_BYTES,
        "PDF exceeds the 128 MiB file limit"
    );
    ensure!(
        scale.is_finite() && scale > 0.0 && scale <= 16.0,
        "Invalid PDF render scale"
    );
    let mut header = Vec::with_capacity(24);
    header.extend_from_slice(REQUEST_MAGIC);
    header.extend_from_slice(&page_index.to_le_bytes());
    header.extend_from_slice(&scale.to_le_bytes());
    header.extend_from_slice(&(length as u64).to_le_bytes());
    Ok(header)
}

pub fn read_response(mut bytes: &[u8]) -> Result<RenderedPage> {
    let mut magic = [0; 8];
    bytes.read_exact(&mut magic)?;
    ensure!(&magic == RESPONSE_MAGIC, "Invalid PDF renderer response");
    let page_count = read_u32(&mut bytes)?;
    let page_width = f32::from_bits(read_u32(&mut bytes)?);
    let page_height = f32::from_bits(read_u32(&mut bytes)?);
    let width = read_u32(&mut bytes)?;
    let height = read_u32(&mut bytes)?;
    ensure!(
        page_count > 0 && page_count as usize <= MAX_PAGES,
        "Invalid PDF page count"
    );
    ensure!(
        valid_page_dimensions(page_width, page_height),
        "Invalid PDF page dimensions"
    );
    ensure!(
        width > 0 && height > 0 && width <= MAX_DIMENSION as u32 && height <= MAX_DIMENSION as u32,
        "Invalid PDF bitmap dimensions"
    );
    let pixels = (width as usize)
        .checked_mul(height as usize)
        .context("PDF bitmap is too large")?;
    ensure!(
        pixels <= MAX_PIXELS && bytes.len() == pixels * 4,
        "Invalid PDF bitmap length"
    );
    Ok(RenderedPage {
        page_count,
        page_width,
        page_height,
        width,
        height,
        rgba: bytes.to_vec(),
    })
}

pub fn render_page(bytes: Vec<u8>, page_index: u32, scale: f32) -> Result<RenderedPage> {
    request_header(page_index, scale, bytes.len())?;
    ensure!(
        bytes.windows(5).take(1024).any(|window| window == b"%PDF-"),
        "This file does not contain a PDF header"
    );
    let pdf = Pdf::new(bytes).map_err(|error| {
        anyhow::anyhow!("Unable to read PDF: {error:?}. Encrypted PDFs are not supported")
    })?;
    let page_count = pdf.pages().len();
    ensure!(page_count > 0, "This PDF has no pages");
    ensure!(page_count <= MAX_PAGES, "PDF exceeds the 10,000 page limit");
    let page = pdf
        .pages()
        .get(page_index as usize)
        .context("PDF page is out of range")?;
    let (page_width, page_height) = page.render_dimensions();
    let scale = bounded_scale(page_width, page_height, scale)?;
    let pixmap = hayro::render(
        page,
        &RenderCache::new(),
        &InterpreterSettings::default(),
        &RenderSettings::default(),
        &PixmapSettings {
            x_scale: scale,
            y_scale: scale,
            bg_color: WHITE,
        },
    );
    Ok(RenderedPage {
        page_count: page_count as u32,
        page_width,
        page_height,
        width: pixmap.width() as u32,
        height: pixmap.height() as u32,
        rgba: pixmap.data_as_u8_slice().to_vec(),
    })
}

fn bounded_scale(width: f32, height: f32, requested: f32) -> Result<f32> {
    ensure!(
        valid_page_dimensions(width, height),
        "PDF page has invalid dimensions"
    );
    let mut scale = requested
        .min(MAX_DIMENSION / width.max(height))
        .min((MAX_PIXELS as f64 / (width as f64 * height as f64)).sqrt() as f32);
    let bitmap_width = (width * scale).ceil();
    let bitmap_height = (height * scale).ceil();
    if bitmap_width > MAX_DIMENSION
        || bitmap_height > MAX_DIMENSION
        || bitmap_width as f64 * bitmap_height as f64 > MAX_PIXELS as f64
    {
        // Rounding both dimensions up can exceed an otherwise exact area bound.
        scale = scale.min((MAX_DIMENSION - 1.0) / width.max(height)).min(
            ((MAX_PIXELS as f64 - 2.0 * MAX_DIMENSION as f64) / (width as f64 * height as f64))
                .sqrt() as f32,
        );
    }
    ensure!(
        scale.is_finite() && width * scale >= 1.0 && height * scale >= 1.0,
        "PDF page dimensions cannot be rendered within the bitmap limit"
    );
    Ok(scale)
}

fn valid_page_dimensions(width: f32, height: f32) -> bool {
    width.is_finite()
        && height.is_finite()
        && width > 0.0
        && height > 0.0
        && width <= MAX_PAGE_DIMENSION
        && height <= MAX_PAGE_DIMENSION
}

pub fn run_worker(mut input: impl Read, mut output: impl Write) -> Result<()> {
    let mut magic = [0; 8];
    input.read_exact(&mut magic)?;
    ensure!(&magic == REQUEST_MAGIC, "Invalid PDF renderer request");
    let page_index = read_u32(&mut input)?;
    let scale = f32::from_bits(read_u32(&mut input)?);
    let mut length = [0; 8];
    input.read_exact(&mut length)?;
    let length = u64::from_le_bytes(length);
    ensure!(
        length <= MAX_FILE_BYTES as u64,
        "PDF exceeds the 128 MiB file limit"
    );
    let mut bytes = vec![0; length as usize];
    input.read_exact(&mut bytes)?;
    // Only this helper process parses PDFs. A parser panic must never unwind into Zed.
    let page = std::panic::catch_unwind(|| render_page(bytes, page_index, scale))
        .map_err(|_| anyhow::anyhow!("The PDF renderer could not process this document"))??;
    output.write_all(RESPONSE_MAGIC)?;
    output.write_all(&page.page_count.to_le_bytes())?;
    output.write_all(&page.page_width.to_le_bytes())?;
    output.write_all(&page.page_height.to_le_bytes())?;
    output.write_all(&page.width.to_le_bytes())?;
    output.write_all(&page.height.to_le_bytes())?;
    output.write_all(&page.rgba)?;
    output.flush()?;
    Ok(())
}

pub fn run_worker_if_invoked() -> bool {
    if std::env::args_os().nth(1).as_deref() != Some(std::ffi::OsStr::new(WORKER_ARGUMENT)) {
        return false;
    }
    match run_worker(std::io::stdin().lock(), std::io::stdout().lock()) {
        Ok(()) => true,
        Err(error) => {
            eprintln!("{error:#}");
            std::process::exit(1);
        }
    }
}

fn read_u32(input: &mut impl Read) -> Result<u32> {
    let mut value = [0; 4];
    input.read_exact(&mut value)?;
    Ok(u32::from_le_bytes(value))
}

#[cfg(test)]
mod tests {
    use super::*;

    const TWO_PAGES: &[u8] = include_bytes!("../tests/fixtures/two-pages.pdf");

    #[test]
    fn renders_text_vectors_and_an_embedded_image() {
        let first = render_page(TWO_PAGES.to_vec(), 0, 1.0).expect("first page");
        let second = render_page(TWO_PAGES.to_vec(), 1, 2.0).expect("second page");
        assert_eq!(first.page_count, 2);
        assert_eq!((first.width, first.height), (200, 300));
        assert_eq!((second.width, second.height), (600, 400));
        assert!(
            first
                .rgba
                .chunks_exact(4)
                .any(|pixel| pixel[0] > 200 && pixel[1] < 20)
        );
        assert!(
            second
                .rgba
                .chunks_exact(4)
                .any(|pixel| pixel[2] > 200 && pixel[0] < 20)
        );
    }

    #[test]
    fn worker_protocol_round_trip() {
        let mut request = request_header(1, 1.0, TWO_PAGES.len()).expect("request");
        request.extend_from_slice(TWO_PAGES);
        let mut output = Vec::new();
        run_worker(request.as_slice(), &mut output).expect("worker");
        let page = read_response(&output).expect("response");
        assert_eq!((page.page_count, page.width, page.height), (2, 300, 200));
        let mut oversized_metadata = output.clone();
        oversized_metadata[12..16].copy_from_slice(&f32::MAX.to_le_bytes());
        assert!(read_response(&oversized_metadata).is_err());
        output.pop();
        assert!(read_response(&output).is_err());
    }

    #[test]
    fn rejects_invalid_and_out_of_range_documents() {
        assert!(render_page(b"not a PDF".to_vec(), 0, 1.0).is_err());
        assert!(render_page(b"%PDF-1.7\ntruncated".to_vec(), 0, 1.0).is_err());
        assert!(render_page(TWO_PAGES.to_vec(), 2, 1.0).is_err());
        assert!(render_page(TWO_PAGES.to_vec(), 0, f32::NAN).is_err());
    }

    #[test]
    fn bounds_allocations_and_rejects_bad_dimensions() {
        assert!(request_header(0, 1.0, MAX_FILE_BYTES + 1).is_err());
        assert!(bounded_scale(f32::INFINITY, 300.0, 1.0).is_err());
        assert!(bounded_scale(0.0, 300.0, 1.0).is_err());
        assert!(bounded_scale(f32::MAX, 300.0, 1.0).is_err());
        assert!(bounded_scale(300.0, MAX_PAGE_DIMENSION + 1.0, 1.0).is_err());
        let scale = bounded_scale(100_000.0, 100_000.0, 8.0).expect("bounded scale");
        assert!((100_000.0 * scale).powi(2) <= MAX_PIXELS as f32);
        let scale = bounded_scale(123_456.0, 78_901.0, 8.0).expect("rounded dimensions");
        assert!(
            (123_456.0 * scale).ceil() as f64 * (78_901.0 * scale).ceil() as f64
                <= MAX_PIXELS as f64
        );
        let mut request = request_header(0, 1.0, 0).expect("request");
        request[16..24].copy_from_slice(&u64::MAX.to_le_bytes());
        assert!(run_worker(request.as_slice(), Vec::new()).is_err());
    }
}
