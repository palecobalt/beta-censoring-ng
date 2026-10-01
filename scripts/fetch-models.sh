#!/bin/sh
# Download the NudeNet v3 models (320n, 640m) from NudeNet's v3.4-weights
# release, and the optional hotscreen model, into models/ and verify them.
# Uses the GitHub API asset endpoint because direct release downloads can
# redirect to a login page.
set -eu
cd "$(dirname "$0")/.."
mkdir -p models
assets=https://api.github.com/repos/notAI-tech/NudeNet/releases/assets
# the revision the checksum belongs to
hotscreen=https://huggingface.co/Perfectfox256/hotscreen-detection-models/resolve/8134f9ea866820684cdb9f022a5ce3d306bed0a2/yolo-07-2025

fetch() {
    name=$1 url=$2 sha=$3
    if [ -f "models/$name" ] && echo "$sha  models/$name" | sha256sum -c --status; then
        echo "models/$name already present"
        return
    fi
    echo "downloading $name"
    curl -fL -H "Accept: application/octet-stream" -o "models/$name.part" "$url"
    echo "$sha  models/$name.part" | sha256sum -c --quiet
    mv "models/$name.part" "models/$name"
}

fetch 320n.onnx "$assets/176831997" c15d8273adad2d0a92f014cc69ab2d6c311a06777a55545f2c4eb46f51911f0f
fetch 640m.onnx "$assets/176832019" 04fe3d77980780c1f8297dc6d7f942fd5b3abe6942a188f742a85241e4f634eb
fetch hs-real-y11n-640-fp32.onnx "$hotscreen/hs-real-y11n-640-fp32.onnx" b2b5adff81442762a93f78cfdad4b492471c92fa4e60fc6d553afffd0dc892d5
