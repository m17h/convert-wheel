# The FFmpeg in Convert Wheel: build, licence and source

| | |
| --- | --- |
| Version | n8.1.3-27-g8dca1586f4-20261031 (branch release/8.1), 64-bit Windows, shared libraries |
| Licence | GNU LGPL version 3 or later (`COPYING.LGPLv3.txt`, which adds to `COPYING.GPLv3.txt`) |
| Built by | FFmpeg-Builds, release `autobuild-2026-10-31-12-59` (a month-end release; the project moved from BtbN/FFmpeg-Builds to FFmpeg-Builds/FFmpeg-Builds) |
| Binary archive | https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n8.1.3-27-g8dca1586f4-win64-lgpl-shared-8.1.zip |
| Archive SHA-256 | `5cef7bc48e513f3cee7c881e8b2999d835541edb1846c3e89443a67f8f8556ab` |
| Shipped from it | ffmpeg.exe, avcodec-62.dll, avdevice-62.dll, avfilter-11.dll, avformat-62.dll, avutil-60.dll, swresample-6.dll, swscale-9.dll, LICENSE.txt (all unmodified; hashes in the repository's `tools\ffmpeg.json`) |

Convert Wheel runs ffmpeg.exe as a separate program. It isn't linked into Convert Wheel.

## Source code

- FFmpeg: https://github.com/FFmpeg/FFmpeg/tree/8dca1586f468bfcbb78ae3ed618af1361d9f140a
  (download: https://github.com/FFmpeg/FFmpeg/archive/8dca1586f468bfcbb78ae3ed618af1361d9f140a.tar.gz)
- The scripts and patches that build it and everything in it:
  https://github.com/FFmpeg-Builds/FFmpeg-Builds/tree/5031c6f1082414e53688d39e521849905071070d
  (`variants/win64-lgpl-shared.sh` with `addins/8.1.sh`; patches for aom and libaribb24 are in `patches/`)
- Each library linked into the DLLs: the table below. A commit can be fetched with
  `git init; git fetch --depth 1 <repository> <commit>; git checkout FETCH_HEAD`.

## Replacing it

You may use your own build of FFmpeg instead:

1. Convert a file that needs FFmpeg (WebM or AVI, say), so it has been unpacked.
2. Exit Convert Wheel (tray icon, Exit).
3. Open `%LOCALAPPDATA%\ConvertWheel\tools`. The folder `ffmpeg-<letters and digits>` holds ffmpeg.exe and the DLLs.
   Replace any of them with compatible files. Leave the `.complete` file there.

Convert Wheel doesn't check or restore those files; it unpacks them again only if ffmpeg.exe or `.complete`
is missing, or an update brings a different FFmpeg. The DLL names carry FFmpeg's interface version, so a DLL
replaced on its own must have the same name; replacing all of them together has no such limit.
You may also modify and reverse engineer them for that purpose, as the LGPL allows.

## Configuration

```
--prefix=/ffbuild/prefix --pkg-config-flags=--static --pkg-config=pkg-config --cross-prefix=x86_64-w64-mingw32- --arch=x86_64 --target-os=mingw32 --enable-version3 --disable-debug --enable-shared --disable-static --disable-w32threads --enable-pthreads --enable-iconv --enable-zlib --enable-libxml2 --enable-libvmaf --enable-fontconfig --enable-libharfbuzz --enable-libfreetype --enable-libfribidi --enable-lcms2 --enable-vulkan --enable-libshaderc --enable-libdav1d --enable-libvorbis --enable-librav1e --enable-librsvg --disable-libxcb --disable-xlib --disable-libpulse --enable-gmp --enable-lzma --enable-liblcevc-dec --enable-opencl --enable-amf --enable-libaom --disable-avisynth --enable-chromaprint --disable-libdavs2 --disable-libdvdread --disable-libdvdnav --disable-libfdk-aac --enable-ffnvcodec --enable-cuda-llvm --disable-frei0r --enable-libgme --enable-libkvazaar --enable-libaribb24 --enable-libaribcaption --enable-libass --enable-libbluray --enable-libjxl --enable-libmp3lame --enable-libopus --enable-libplacebo --enable-librist --enable-libssh --enable-libtheora --enable-libvpx --enable-libwebp --enable-libzmq --enable-lv2 --enable-libvpl --enable-openal --enable-liboapv --enable-libopencore-amrnb --enable-libopencore-amrwb --enable-libopenh264 --enable-libopenjpeg --enable-libopenmpt --disable-librubberband --enable-schannel --enable-sdl2 --enable-libsnappy --enable-libsoxr --enable-libsrt --enable-libsvtav1 --enable-libtwolame --enable-libuavs3d --disable-libdrm --enable-vaapi --enable-vapoursynth --disable-libvidstab --enable-libvvenc --disable-whisper --disable-libx264 --disable-libx265 --disable-libxavs2 --disable-libxvid --enable-libzimg --enable-libzvbi --extra-cflags=-DLIBTWOLAME_STATIC --extra-cxxflags= --extra-libs=-lgomp --extra-ldflags=-pthread --extra-ldexeflags= --cc=x86_64-w64-mingw32-gcc --cxx=x86_64-w64-mingw32-g++ --ar=x86_64-w64-mingw32-gcc-ar --ranlib=x86_64-w64-mingw32-gcc-ranlib --nm=x86_64-w64-mingw32-gcc-nm --extra-version=20261031
```

Not in it: `--enable-gpl` and `--enable-nonfree` (and so no x264, x265, Xvid, fdk-aac, Rubber Band, vid.stab,
frei0r, AviSynth, davs2/xavs2 or libdvdnav). `ffmpeg -L` states LGPL version 3 or later.

## Libraries linked into it

Licence texts are in `libraries`. Where a library offers a choice, the choice named here applies.

| Library (name of its build script) | Licence | Source | Commit |
| --- | --- | --- | --- |
| mingw-std-threads | BSD-2-Clause | https://github.com/meganz/mingw-std-threads.git | `c931bac289dd431f1dd30fc4a5d1a7be36668073` |
| mingw | ZPL-2.1 and other permissive (mingw-w64 runtime, winpthreads MIT/BSD) | https://git.code.sf.net/p/mingw-w64/mingw-w64.git | `7213d719fe344553aa24fef7cc7dedee4940d594` |
| libiconv | LGPL-2.1-or-later | https://git.savannah.gnu.org/git/libiconv.git | `b5a1735e02bf43ee6e490fcf9395e8abc8d32432` |
| libiconv-2 | gnulib: LGPL-2.1-or-later for the modules libiconv uses | https://git.savannah.gnu.org/git/gnulib.git | `5df606bbd0ef54a8f150c3303c870b9381a661f0` |
| zlib | Zlib | https://github.com/madler/zlib.git | `9d314c341b2ce16e01ae44e27ec3dd2f32aa58fd` |
| fribidi | LGPL-2.1-or-later | https://github.com/fribidi/fribidi.git | `d2f3e1f4946e7cef9789e1c3ebdb4a60d4314c7f` |
| gmp | LGPL-3.0-or-later or GPL-2.0-or-later | https://github.com/BtbN/gmplib.git | `5c3e9a9d52b697cab7b7d26a53972a3bf922d59a` |
| libogg | BSD-3-Clause | https://github.com/xiph/ogg.git | `06a5e0262cdc28aa4ae6797627a783b5010440f0` |
| libpng | libpng-2.0 | https://github.com/glennrp/libpng.git | `0b0876829d77db0827a8cc2c4326f63be22b2aa1` |
| libxml2 | MIT | https://github.com/GNOME/libxml2.git | `c79c9612fcc77b06b0d16f35fd363d938bdbbf5e` |
| openssl | Apache-2.0 | https://github.com/openssl/openssl.git | `openssl-3.6.5` |
| xz | 0BSD (liblzma) | https://github.com/tukaani-project/xz.git | `299eb5c3f597f107fd6019bf156885ac8bfaa5ea` |
| brotli | MIT | https://github.com/google/brotli.git | `2e2bcf17ae7115d84ef6588d9a3651cc987d73e7` |
| fontconfig | MIT-style | https://gitlab.freedesktop.org/fontconfig/fontconfig.git | `b25ffeabd15b76f86c0c6736f6df2f1b89858010` |
| harfbuzz | MIT (Old MIT) | https://github.com/harfbuzz/harfbuzz.git | `75d8ac5d2313842550174eaab0cbaf653c8f2f77` |
| freetype | FTL or GPL-2.0-only (used under the FTL) | https://gitlab.freedesktop.org/freetype/freetype.git | `4ba7c3d82ab8b3cc80b869337d2c2b1913c5e415` |
| lcms2 | MIT | https://github.com/mm2/Little-CMS.git | `1112313b3784ced9297d8c0ee076aba17499827a` |
| libffi | MIT | https://github.com/libffi/libffi.git | `bc553867367246d140cd156f060bd0409f57f157` |
| libudfread | LGPL-2.1-or-later | https://code.videolan.org/videolan/libudfread.git | `b0bc6957e7e07d5f35391f210596d9eb71cffdd9` |
| libunibreak | Zlib | https://github.com/adah1972/libunibreak.git | `f8172fe771ef620112247cadfb876aecaa4f769e` |
| libvorbis | BSD-3-Clause | https://github.com/xiph/vorbis.git | `1b75110b5a2754ba1931d82dd83cb822b266a21d` |
| opencl | Apache-2.0 | https://github.com/KhronosGroup/OpenCL-Headers.git | `82f60316cc142e7b21dc1d8983b26de18858fbd9` |
| opencl-2 | Apache-2.0 | https://github.com/KhronosGroup/OpenCL-ICD-Loader.git | `5192c84f8059e5f703e5452929b613f9487f6e4c` |
| pcre2 | BSD-3-Clause | https://github.com/PCRE2Project/pcre2.git | `be940c4eaec0bc4117e06e8f14e56bf82089be6b` |
| pixman | MIT | https://gitlab.freedesktop.org/pixman/pixman.git | `e7fd44140fe6edef507d54bdbc25c09543ffc936` |
| vmaf | BSD-2-Clause-Patent | https://github.com/Netflix/vmaf.git | `f712fa7564fe49b12991d83920b83573af2ae063` |
| xxhash | BSD-2-Clause | https://github.com/Cyan4973/xxHash.git | `61fd0fece2ebafc5bd9b573050e0c046c4e48d28` |
| zstd | BSD-3-Clause or GPL-2.0-only (used under the BSD licence) | https://github.com/facebook/zstd.git | `49cf51799ea051457f57243527aa2058f999f005` |
| vulkan-headers | Apache-2.0 or MIT | https://github.com/KhronosGroup/Vulkan-Headers.git | `v1.4.366` |
| vulkan-loader | MIT | https://github.com/BtbN/Vulkan-Shim-Loader.git | `65b3936528cd92eb4ea3de485d03f858a3850484` |
| vulkan-loader-2 | Apache-2.0 or MIT | https://github.com/KhronosGroup/Vulkan-Headers.git | `v1.4.366` |
| shaderc | Apache-2.0 | https://github.com/google/shaderc.git | `2737994b552137a85545f956e303d620e6ff232c` |
| spirv-cross | Apache-2.0 or MIT | https://github.com/KhronosGroup/SPIRV-Cross.git | `300681bbcb8365a8d1eacdacdbdd0249bbf1d6b1` |
| spirv-headers | MIT | https://github.com/KhronosGroup/SPIRV-Headers.git | `fbf86836a9b1966586faf7be7bb8a6440e156d62` |
| amf | MIT | https://github.com/GPUOpen-LibrariesAndSDKs/AMF.git | `57ee0cfb0e1a08d9041f77435653794c1b012be0` |
| aom | BSD-2-Clause and the Alliance for Open Media patent licence | https://aomedia.googlesource.com/aom | `9ace39e02a34992b83fc7f7f2bfbbd2e79348c74` |
| chromaprint | MIT with LGPL-2.1-or-later parts | https://github.com/acoustid/chromaprint.git | `3b38c100022bdfb49be7797c4828a476f1621d55` |
| dav1d | BSD-2-Clause | https://code.videolan.org/videolan/dav1d.git | `b8b2d726b4b78a2fb174cb45baff441b5969672c` |
| ffnvcodec | MIT | https://github.com/FFmpeg/nv-codec-headers.git | `f2053071ea93137255b6dcf42e9b4c9e0965a44e` |
| ffnvcodec-2 | MIT | https://github.com/FFmpeg/nv-codec-headers.git | `ced4f8eba3ba5dd431932cba17928f0dffdaeb2b` |
| ffnvcodec-3 | MIT | https://github.com/FFmpeg/nv-codec-headers.git | `833faee5f7b8d3f56444347c587f4aed11ee213f` |
| gme | LGPL-2.1-or-later (built with the Nuked YM2612 emulator, not the GPL one) | https://github.com/libgme/game-music-emu.git | `f68963b1de0633149b05732113d3c3113c6f63a1` |
| kvazaar | BSD-3-Clause | https://github.com/ultravideo/kvazaar.git | `1f11f9ae33ef0cbe48a7d335b7144af1ecfe8092` |
| lcevcdec | BSD-3-Clause-Clear (no patent licence) | https://github.com/v-novaltd/LCEVCdec.git | `741fb7c869cf61012f7dcebfcd9e56981198c78b` |
| libaribb24 | LGPL-3.0 | https://github.com/nkoriyama/aribb24.git | `5e9be272f96e00f15a2f3c5f8ba7e124862aec38` |
| libaribcaption | MIT | https://github.com/xqq/libaribcaption.git | `37dde0f32652c32aa98bf92278944d1cfb2e7354` |
| libass | ISC | https://github.com/libass/libass.git | `f61db567e6593df3470e91594bcd4ad2d0473aff` |
| libbluray | LGPL-2.1-or-later | https://code.videolan.org/videolan/libbluray.git | `f33a738fd7bb8b5e1148b0a5424778db7342450b` |
| libjxl | BSD-3-Clause and a patent grant | https://github.com/libjxl/libjxl.git | `e47d2a2b5bb418f2947ce07f36cb048861db3820` |
| libmp3lame | LGPL-2.0-or-later | https://svn.code.sf.net/p/lame/svn/trunk/lame | `7113` |
| libopus | BSD-3-Clause | https://github.com/xiph/opus.git | `a8b2545eec71e0f53ecead2d583e7b6baa42684d` |
| libplacebo | LGPL-2.1-or-later | https://code.videolan.org/videolan/libplacebo.git | `275f7dea27924cdd34ab2e6c20e448b6bc4fab65` |
| mbedtls | Apache-2.0 or GPL-2.0-or-later (used under Apache-2.0) | https://github.com/ARMmbed/mbedtls.git | `v4.2.0` |
| librist | BSD-2-Clause | https://code.videolan.org/rist/librist.git | `47ad5513d1aa0adb692d78f635512e7fa123f06e` |
| glib | LGPL-2.1-or-later | https://github.com/GNOME/glib.git | `50b938a3e8f5154a25b5af3dd8b9dd8b2ccba7c3` |
| cairo | LGPL-2.1 or MPL-1.1 | https://gitlab.freedesktop.org/cairo/cairo.git | `74755964edef651691d5cd36ff140108298e4e6e` |
| pango | LGPL-2.0-or-later | https://github.com/GNOME/pango.git | `68e981d243d50e03f3df12fb400f31647ef570d2` |
| librsvg | LGPL-2.1-or-later | https://github.com/GNOME/librsvg.git | `0c074d0a25adefd65843067306f9bc1fb609f68f` |
| libssh | LGPL-2.1 with OpenSSL exception | https://gitlab.com/libssh/libssh-mirror.git | `9de02f3d01ba16d06f46c0c4d27aa08d8deeecee` |
| libtheora | BSD-3-Clause | https://github.com/xiph/theora.git | `28fd5ec77f0ad0e07a371cef1047828116f6bd8a` |
| libvpx | BSD-3-Clause and a patent grant | https://chromium.googlesource.com/webm/libvpx | `900b2a52b69e9d4ab72eb855c365da1100c50eb1` |
| libwebp | BSD-3-Clause and a patent grant | https://chromium.googlesource.com/webm/libwebp | `1844042d1ca751e62e61df7617dba19c98588339` |
| libzmq | MPL-2.0 | https://github.com/zeromq/libzmq.git | `46493370217ac135246617fa2f6ac819d8b61bfc` |
| lv2 | ISC | https://github.com/lv2/lv2.git | `3c57dae600a5ad8d05acd53ee3490f7d91cb7be6` |
| serd | ISC | https://github.com/drobilla/serd.git | `b621b0bc790d1e8102d3138ead16b974ddda8432` |
| zix | ISC | https://github.com/drobilla/zix.git | `17d632393f5addb6fa24dda2927b96515407cca7` |
| sord | ISC | https://github.com/drobilla/sord.git | `4b5232bb91e7f2f1b84df384494d073fabbcd312` |
| sratom | ISC | https://github.com/lv2/sratom.git | `9e8ee84eed502e6030ce8bb49694fd28542b08fc` |
| lilv | ISC | https://github.com/lv2/lilv.git | `4b8f30055fa5cb4134a2eb45fc1555bc04289314` |
| onevpl | MIT | https://github.com/intel/libvpl.git | `674d015bcb294bc39fa276e99a652ea045423e82` |
| openal | LGPL-2.0-or-later | https://github.com/kcat/openal-soft.git | `4a52288b772d1722713675f71bb30f321e78fd80` |
| openapv | BSD-3-Clause | https://github.com/AcademySoftwareFoundation/openapv.git | `b4ff69856b4c3e8849df431ee3db54fd535f9bf3` |
| opencore-amr | Apache-2.0 | https://git.code.sf.net/p/opencore-amr/code | `7dba8c32238418ce0b316a852b2224df586ca896` |
| openh264 | BSD-2-Clause | https://github.com/cisco/openh264.git | `1bca62311e447fd9c39fc93de2c4b96cd5ce971c` |
| openjpeg | BSD-2-Clause | https://github.com/uclouvain/openjpeg.git | `8314119b067c0fc77834731168daaebd379fdb12` |
| openmpt | BSD-3-Clause | https://github.com/OpenMPT/openmpt.git | `3ee3c8000b45886703816fd027369ab6ad6fc6cb` |
| rav1e | BSD-2-Clause and the Alliance for Open Media patent licence | https://github.com/xiph/rav1e.git | `31435de9d76fddd38f6dcc31d4014574cebb2092` |
| sdl | Zlib | https://github.com/libsdl-org/SDL.git | `8acf7cfc0f88717e5c036f60f0314bfcafe39189` |
| snappy | BSD-3-Clause | https://github.com/google/snappy.git | `946606459314e55d029c85f9a8c46f8809467f0f` |
| soxr | LGPL-2.1-or-later | https://git.code.sf.net/p/soxr/code | `945b592b70470e29f917f4de89b4281fbbd540c0` |
| srt | MPL-2.0 | https://github.com/Haivision/srt.git | `9f2390232a8256ed4c72a5418c2b2777c49ad737` |
| svtav1 | BSD-3-Clause-Clear and the Alliance for Open Media patent licence | https://gitlab.com/AOMediaCodec/SVT-AV1.git | `f2c3fb78611d542b12b11c6d10d3744b31b754a0` |
| twolame | LGPL-2.1-or-later | https://github.com/njh/twolame.git | `6fced852d4d5cfad58cf9dbe3ea619b08e87d398` |
| uavs3d | BSD-3-Clause | https://github.com/uavs3/uavs3d.git | `0e20d2c291853f196c68922a264bcd8471d75b68` |
| libva | MIT | https://github.com/intel/libva.git | `f052e984e0c8bf10784198cc311b23c567b1367f` |
| vapoursynth | LGPL-2.1-or-later (headers only; loaded at run time if the user has it) | https://github.com/vapoursynth/vapoursynth.git | `R81` |
| vvenc | BSD-3-Clause-Clear (no patent licence) | https://github.com/fraunhoferhhi/vvenc.git | `0e7508b811bbea3cd510956ddb3193f3a4c93684` |
| zimg | WTFPL | https://github.com/sekrit-twc/zimg.git | `67e0603271c080e22c8429856dd4a8a56587e61e` |
| zvbi | LGPL-2.0-or-later, but packet-830.* and pdc.* are marked GPL-2.0-only: see below | https://github.com/zapping-vbi/zvbi | `34bb6f660c5c1bacd4a66cc7b22311e4ca3c1ab5` |

Also linked in, through the ones above: KISS FFT (BSD-3-Clause, in Chromaprint), glslang `73b25650df6dc4545966674531e6b7cc8f5d2407`
(several permissive licences) and SPIRV-Tools `71f287f18ed78d496e724fbfb1c3feae3a2af75a` (Apache-2.0) through shaderc, Highway
(Apache-2.0 or BSD-3-Clause) through libjxl, TF-PSA-Crypto (Apache-2.0 or GPL-2.0-or-later) through Mbed TLS, the Rust crates named in
the `Cargo.lock` of rav1e and librsvg (permissive; their notices come with those sources), and GCC's runtime libraries
(libgcc, libstdc++, libgomp; GPL-3.0 with the GCC Runtime Library Exception, `libraries/gcc-runtime.txt`).
LAME's "commit" is the Subversion revision; its text is `libraries/lame.txt`.

### libzvbi

libzvbi is LGPL as a library, but its own COPYING file marks `src/packet-830.*` and `src/pdc.*` "GPL-2"
(only). They belong to libzvbi's sources and other parts of it call them, so compiled code from them may be in avcodec-62.dll.
FFmpeg and FFmpeg-Builds treat libzvbi as LGPL. Its full source is available through the table above.
