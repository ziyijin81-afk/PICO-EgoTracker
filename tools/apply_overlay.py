"""Copy this source overlay into a separately prepared Unity project."""
import argparse
import shutil
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path, help="Existing Unity project; selected files will be replaced")
args = parser.parse_args()
target = args.project.resolve()
source = Path(__file__).resolve().parents[1] / "unity-overlay"
if not (target / "Assets").is_dir() or not (target / "Packages").is_dir():
    parser.error("target must contain Assets and Packages; prepare the base project first")
if target == source or source in target.parents or target in source.parents:
    parser.error("use a separate base project, not this repository")
# The renamed generic class replaces the previous right-only implementation.
for suffix in ("", ".meta"):
    (target / "Assets/EgoCapture/Scripts" / ("EgoRightWristFusion.cs" + suffix)).unlink(missing_ok=True)
count = 0
for original in sorted(source.rglob("*")):
    if original.is_file():
        destination = target / original.relative_to(source)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, destination)
        count += 1
print(f"Copied {count} source/config files to {target}. No build, installation, or recording performed.")
