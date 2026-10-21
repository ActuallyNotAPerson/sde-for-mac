#!/bin/sh
# Prepares the tree for the public repository: a copy of the current commit without what tools/public-exclude.txt
# lists (FEMA's decompiled code, the answer-key project, private notes). The copy is a fresh git repository with one
# commit, so none of the private history (which contains FEMA's decompiled code) is published.
#
# Usage: tools/make-public-tree.sh /path/to/new/folder [commit message]
# The folder must not exist yet. Nothing is pushed; see docs/RELEASING.md for the rest.
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:?Give the folder to create, e.g. ~/Projects/sde-public}"
MESSAGE="${2:-SDE for Mac 2.0}"
[ ! -e "$TARGET" ] || { echo "$TARGET already exists; choose a new folder." >&2; exit 1; }
[ -z "$(git -C "$ROOT" status --porcelain)" ] || { echo "Commit or stash your changes first: the copy is made from the last commit." >&2; exit 1; }

mkdir -p "$TARGET"
git -C "$ROOT" archive --format=tar HEAD | tar -x -C "$TARGET"
grep -v '^#' "$ROOT/tools/public-exclude.txt" | grep -v '^[[:space:]]*$' | while read -r path; do
  rm -rf "$TARGET/$path"
done

# Anything left that mentions the decompiled code by path is a reference, which is fine; the code itself must be gone.
for must_be_gone in decomp reference/OriginalCalc; do
  if [ -e "$TARGET/$must_be_gone" ]; then
    echo "$must_be_gone is still in the copy; check tools/public-exclude.txt." >&2
    exit 1
  fi
done

git -C "$TARGET" init -q -b main
git -C "$TARGET" add -A
git -C "$TARGET" commit -q -m "$MESSAGE"
echo "Prepared $TARGET ($(git -C "$TARGET" ls-files | wc -l | tr -d ' ') files, one commit). Review it, then follow docs/RELEASING.md to publish."
