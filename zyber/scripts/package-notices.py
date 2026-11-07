"""Copy dependency metadata and available license texts into a published build."""
import json
from pathlib import Path
import shutil
import sys

root = Path(__file__).resolve().parents[1]
output = Path(sys.argv[1]).resolve()
output.mkdir(parents=True, exist_ok=True)
shutil.copy2(root / "LICENSE", output / "LICENSE")
shutil.copy2(root / "README.md", output / "README.md")
shutil.copy2(root / "FEATURE_PARITY.md", output / "FEATURE_PARITY.md")
notices = output / "licenses"
shutil.copytree(root / "licenses", notices, dirs_exist_ok=True)
shutil.copy2(root / "src/Zyber/Assets/Lilex-LICENSE.txt", notices / "Lilex.txt")
assets = json.loads((root / "src/Zyber/obj/project.assets.json").read_text())
package_root = Path(next(iter(assets["packageFolders"])))
packages = []
for identifier, library in assets["libraries"].items():
    if library["type"] != "package":
        continue
    packages.append(identifier)
    source = package_root / library["path"]
    destination = notices / identifier.replace("/", "-")
    destination.mkdir(exist_ok=True)
    for relative in library.get("files", []):
        name = Path(relative).name.lower()
        if any(word in name for word in ("license", "notice", "copying")) or name.endswith(".nuspec"):
            file = source / relative
            if file.is_file():
                target = destination / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, target)
# Runtime packs are downloaded separately from ordinary PackageReference libraries.
for pack in package_root.glob("microsoft.netcore.app.runtime.*/*"):
    for file in pack.iterdir():
        if file.is_file() and any(word in file.name.lower() for word in ("license", "notice")):
            destination = notices / (pack.parent.name + "-" + pack.name)
            destination.mkdir(exist_ok=True)
            shutil.copy2(file, destination / file.name)
(output / "DEPENDENCIES.txt").write_text("\n".join(sorted(packages)) + "\n")
