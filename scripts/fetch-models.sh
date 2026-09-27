#!/bin/sh
# Download the NudeNet v3 models (320n, 640m) from NudeNet's v3.4-weights
# release into models/ and verify them. Uses the GitHub API asset endpoint
# because direct release downloads can redirect to a login page.
set -eu
cd "$(dirname "$0")/.."
mkdir -p models
assets=https://api.github.com/repos/notAI-tech/NudeNet/releases/assets

fetch() {
    name=$1 id=$2 sha=$3
    if [ -f "models/$name" ] && echo "$sha  models/$name" | sha256sum -c --status; then
        echo "models/$name already present"
        return
    fi
    echo "downloading $name"
    curl -fL -H "Accept: application/octet-stream" -o "models/$name.part" "$assets/$id"
    echo "$sha  models/$name.part" | sha256sum -c --quiet
    mv "models/$name.part" "models/$name"
}

fetch 320n.onnx 176831997 c15d8273adad2d0a92f014cc69ab2d6c311a06777a55545f2c4eb46f51911f0f
fetch 640m.onnx 176832019 04fe3d77980780c1f8297dc6d7f942fd5b3abe6942a188f742a85241e4f634eb
