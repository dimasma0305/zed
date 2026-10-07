fn main() {
    if !pdf_renderer::run_worker_if_invoked() {
        eprintln!("Use --pdf-render-worker with the PDF worker protocol on stdin");
        std::process::exit(1);
    }
}
