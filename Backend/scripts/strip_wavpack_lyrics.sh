#!/usr/bin/env bash
# Delete the APEv2 LYRICS item from every .wv in the library -- the ripper wrote only the
# first line of each song there, and the real synced lyrics now sit in .lrc files beside
# the audio. Lists the files it would touch; pass --apply to actually write.
#
# wvtag rewrites the tag block only, so the audio stays bit-identical and files without a
# LYRICS item are left untouched (it warns and exits 0).
set -euo pipefail

LIBRARY=${MUSIC_LIBRARY_PATH:-/nvme0/DiscordBot/Music Database}

mapfile -d '' -t tagged < <(
    find "$LIBRARY" -name '*.wv' -print0 |
        xargs -0 -P 8 -n 1 sh -c 'wvtag -l "$0" 2>/dev/null | grep -q "^LYRICS:" && printf "%s\0" "$0"' || true
)

printf '%d of %d .wv files carry a LYRICS tag\n' "${#tagged[@]}" "$(find "$LIBRARY" -name '*.wv' | wc -l)"
((${#tagged[@]})) || exit 0

if [[ ${1-} != --apply ]]; then
    printf '%s\n' "${tagged[@]}"
    echo "dry run -- re-run with --apply to strip them"
    exit 0
fi

printf '%s\0' "${tagged[@]}" | xargs -0 -P 8 -n 20 wvtag -q -d LYRICS
echo "stripped ${#tagged[@]} files"
