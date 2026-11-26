# The FFmpeg in Convert Wheel: build, licence and source

| | |
| --- | --- |
| Version | n8.1.3-41-ga8029b6528-20261125 (branch release/8.1), 64-bit Windows, shared libraries |
| Licence | GNU LGPL version 3 or later (`COPYING.LGPLv3.txt`, which adds to `COPYING.GPLv3.txt`) |
| Built by | FFmpeg-Builds, release `autobuild-2026-11-25-12-41` (made after FFmpeg-Builds limited libzvbi to its nonfree builds on 7 November 2026; the project moved from BtbN/FFmpeg-Builds to FFmpeg-Builds/FFmpeg-Builds) |
| Binary archive | https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-11-25-12-41/ffmpeg-n8.1.3-41-ga8029b6528-win64-lgpl-shared-8.1.zip |
| Archive SHA-256 | `03ebc42b7c3956d5397f1033f4caf56d453b99908d47b573ba6c87e4dd72dc43` |
| Shipped from it | ffmpeg.exe, avcodec-62.dll, avdevice-62.dll, avfilter-11.dll, avformat-62.dll, avutil-60.dll, swresample-6.dll, swscale-9.dll, LICENSE.txt (all unmodified; hashes in the repository's `tools\ffmpeg.json`) |

Convert Wheel runs ffmpeg.exe as a separate program. It isn't linked into Convert Wheel.

## Source code

- FFmpeg: https://github.com/FFmpeg/FFmpeg/tree/a8029b6528fecbcb5d99b331cfc76e6467b9bc77
  (download: https://github.com/FFmpeg/FFmpeg/archive/a8029b6528fecbcb5d99b331cfc76e6467b9bc77.tar.gz)
- The scripts and patches that build it and everything in it:
  https://github.com/FFmpeg-Builds/FFmpeg-Builds/tree/b9ebe81995b58cd4e3177a6c0ad5dc2392f7f7fd
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
--prefix=/ffbuild/prefix --pkg-config-flags=--static --pkg-config=pkg-config --cross-prefix=x86_64-w64-mingw32- --arch=x86_64 --target-os=mingw32 --enable-version3 --disable-debug --enable-shared --disable-static --disable-w32threads --enable-pthreads --enable-iconv --enable-zlib --enable-libxml2 --enable-libvmaf --enable-fontconfig --enable-libharfbuzz --enable-libfreetype --enable-libfribidi --enable-lcms2 --enable-vulkan --enable-libshaderc --enable-libdav1d --enable-libvorbis --enable-librav1e --enable-librsvg --disable-libxcb --disable-xlib --disable-libpulse --enable-gmp --enable-lzma --enable-liblcevc-dec --enable-opencl --enable-amf --enable-libaom --disable-avisynth --enable-chromaprint --disable-libdavs2 --disable-libdvdread --disable-libdvdnav --disable-libfdk-aac --enable-ffnvcodec --enable-cuda-llvm --disable-frei0r --enable-libgme --enable-libkvazaar --enable-libaribb24 --enable-libaribcaption --enable-libass --enable-libbluray --enable-libjxl --enable-libmp3lame --enable-libopus --enable-libplacebo --enable-librist --enable-libssh --enable-libtheora --enable-libvpx --enable-libwebp --enable-libzmq --enable-lv2 --enable-libvpl --enable-openal --enable-liboapv --enable-libopencore-amrnb --enable-libopencore-amrwb --enable-libopenh264 --enable-libopenjpeg --enable-libopenmpt --disable-librubberband --enable-schannel --enable-sdl2 --enable-libsnappy --enable-libsoxr --enable-libsrt --enable-libsvtav1 --enable-libtwolame --enable-libuavs3d --disable-libdrm --enable-vaapi --enable-vapoursynth --disable-libvidstab --enable-libvvenc --disable-whisper --disable-libx264 --disable-libx265 --disable-libxavs2 --disable-libxvid --enable-libzimg --disable-libzvbi --extra-cflags=-DLIBTWOLAME_STATIC --extra-cxxflags= --extra-libs=-lgomp --extra-ldflags=-pthread --extra-ldexeflags= --cc=x86_64-w64-mingw32-gcc --cxx=x86_64-w64-mingw32-g++ --ar=x86_64-w64-mingw32-gcc-ar --ranlib=x86_64-w64-mingw32-gcc-ranlib --nm=x86_64-w64-mingw32-gcc-nm --extra-version=20261125
```

Not in it: `--enable-gpl` and `--enable-nonfree` (and so no x264, x265, Xvid, fdk-aac, Rubber Band, vid.stab,
frei0r, AviSynth, davs2/xavs2, libdvdnav or libzvbi). `ffmpeg -L` states LGPL version 3 or later.

## Libraries linked into it

Licence texts are in `libraries`. Where a library offers a choice, the choice named here applies.

| Library (name of its build script) | Licence | Source | Commit |
| --- | --- | --- | --- |
| mingw-std-threads | BSD-2-Clause | https://github.com/meganz/mingw-std-threads.git | `c931bac289dd431f1dd30fc4a5d1a7be36668073` |
| mingw | ZPL-2.1 and other permissive (mingw-w64 runtime, winpthreads MIT/BSD) | https://git.code.sf.net/p/mingw-w64/mingw-w64.git | `41eb042358a52011b35350392130dcdc94a8ead8` |
| libiconv | LGPL-2.1-or-later | https://git.savannah.gnu.org/git/libiconv.git | `baaba39736e3b498e1597bf0e5e899b1f753fb01` |
| libiconv-2 | gnulib: LGPL-2.1-or-later for the modules libiconv uses | https://git.savannah.gnu.org/git/gnulib.git | `f9e4c24e9bf3f878a11505a8d06bec965f0fca02` |
| zlib | Zlib | https://github.com/madler/zlib.git | `99623ef7097352da7993ba614161fd6a311e046e` |
| fribidi | LGPL-2.1-or-later | https://github.com/fribidi/fribidi.git | `15393e4937aa95bcf865d92143735746aeb654fa` |
| gmp | LGPL-3.0-or-later or GPL-2.0-or-later | https://github.com/BtbN/gmplib.git | `f9095918ead819c0a2340232891b5acac24b8085` |
| libogg | BSD-3-Clause | https://github.com/xiph/ogg.git | `06a5e0262cdc28aa4ae6797627a783b5010440f0` |
| libpng | libpng-2.0 | https://github.com/glennrp/libpng.git | `5408ee7a2e56555cc4b0acc4051ae241fee38cf3` |
| libxml2 | MIT | https://github.com/GNOME/libxml2.git | `21c8223ac091b81d5914c52eb20b1b063e29cb92` |
| openssl | Apache-2.0 | https://github.com/openssl/openssl.git | `openssl-3.6.5` |
| xz | 0BSD (liblzma) | https://github.com/tukaani-project/xz.git | `f54febc540607b3bdee0a0e6793355df2a7e9f61` |
| brotli | MIT | https://github.com/google/brotli.git | `3dce8720b74194027c7b4cf42dee5bd159786df5` |
| fontconfig | MIT-style | https://gitlab.freedesktop.org/fontconfig/fontconfig.git | `bb21be622280981392c0335fc22202fbba46d247` |
| harfbuzz | MIT (Old MIT) | https://github.com/harfbuzz/harfbuzz.git | `8f1a4e0634a35182e5dd04401edcf57acf5eb12e` |
| freetype | FTL or GPL-2.0-only (used under the FTL) | https://gitlab.freedesktop.org/freetype/freetype.git | `dbb00e529e69a2be629a9ef98498e4f9b188aab1` |
| lcms2 | MIT | https://github.com/mm2/Little-CMS.git | `ed288c301bd85eaabcb9e1ae0f73540c5f129e1e` |
| libffi | MIT | https://github.com/libffi/libffi.git | `de7afe7aa9e8ab7ac36429428a7222caae1e7988` |
| libsamplerate | BSD-2-Clause (only in ffplay.exe, through SDL: see below) | https://github.com/libsndfile/libsamplerate.git | `0844c208f683527c08ea8a80acc13b398aa9c8bf` |
| libudfread | LGPL-2.1-or-later | https://code.videolan.org/videolan/libudfread.git | `b0bc6957e7e07d5f35391f210596d9eb71cffdd9` |
| libunibreak | Zlib | https://github.com/adah1972/libunibreak.git | `ff24f284a567a006c183b3507b6c650d6ec68b94` |
| libvorbis | BSD-3-Clause | https://github.com/xiph/vorbis.git | `1b75110b5a2754ba1931d82dd83cb822b266a21d` |
| opencl | Apache-2.0 | https://github.com/KhronosGroup/OpenCL-Headers.git | `82f60316cc142e7b21dc1d8983b26de18858fbd9` |
| opencl-2 | Apache-2.0 | https://github.com/KhronosGroup/OpenCL-ICD-Loader.git | `d0be526adb156de7f701024acc811182c984d4c5` |
| pcre2 | BSD-3-Clause | https://github.com/PCRE2Project/pcre2.git | `49d26bca00694ae940adde6d8bd056d8bfc168fa` |
| pixman | MIT | https://gitlab.freedesktop.org/pixman/pixman.git | `b88b92e512a33a222e08352e87ff90a57d33c7b5` |
| vmaf | BSD-2-Clause-Patent | https://github.com/Netflix/vmaf.git | `cf677baaa039723876fff8c2677164f25102e684` |
| xxhash | BSD-2-Clause | https://github.com/Cyan4973/xxHash.git | `2ec67caeb18ec15ae60c0853174331089acddfc0` |
| vulkan-headers | Apache-2.0 or MIT | https://github.com/KhronosGroup/Vulkan-Headers.git | `v1.4.368` |
| vulkan-loader | MIT | https://github.com/BtbN/Vulkan-Shim-Loader.git | `65b3936528cd92eb4ea3de485d03f858a3850484` |
| vulkan-loader-2 | Apache-2.0 or MIT | https://github.com/KhronosGroup/Vulkan-Headers.git | `v1.4.368` |
| shaderc | Apache-2.0 | https://github.com/google/shaderc.git | `05814952134d09f8b54ae925afe578fc341cf6c1` |
| spirv-cross | Apache-2.0 or MIT | https://github.com/KhronosGroup/SPIRV-Cross.git | `57e4efcc84112abcd426dc5ec248b5339bee04f2` |
| spirv-headers | MIT | https://github.com/KhronosGroup/SPIRV-Headers.git | `5c6004e758e0c80185ef2cee93ae5c10fa342a5b` |
| amf | MIT | https://github.com/GPUOpen-LibrariesAndSDKs/AMF.git | `57ee0cfb0e1a08d9041f77435653794c1b012be0` |
| aom | BSD-2-Clause and the Alliance for Open Media patent licence | https://aomedia.googlesource.com/aom | `aac27b8f291ed449fac576bcce0ad0ca189be851` |
| chromaprint | MIT with LGPL-2.1-or-later parts | https://github.com/acoustid/chromaprint.git | `3b38c100022bdfb49be7797c4828a476f1621d55` |
| dav1d | BSD-2-Clause | https://code.videolan.org/videolan/dav1d.git | `6c7bee685677fd997a431302be4fea8ea4017606` |
| ffnvcodec | MIT | https://github.com/FFmpeg/nv-codec-headers.git | `f2053071ea93137255b6dcf42e9b4c9e0965a44e` |
| ffnvcodec-2 | MIT | https://github.com/FFmpeg/nv-codec-headers.git | `ced4f8eba3ba5dd431932cba17928f0dffdaeb2b` |
| ffnvcodec-3 | MIT | https://github.com/FFmpeg/nv-codec-headers.git | `833faee5f7b8d3f56444347c587f4aed11ee213f` |
| gme | LGPL-2.1-or-later (built with the Nuked YM2612 emulator, not the GPL one) | https://github.com/libgme/game-music-emu.git | `8238afd9503a59ffcec0e2685584d5ce819ad7c6` |
| kvazaar | BSD-3-Clause | https://github.com/ultravideo/kvazaar.git | `ec7562da7f5971f9fa3f984db358371e74bf3c7d` |
| lcevcdec | BSD-3-Clause-Clear (no patent licence) | https://github.com/v-novaltd/LCEVCdec.git | `e295d8438ba554bc541fb269baa9cbd567b626a8` |
| libaribb24 | LGPL-3.0 | https://github.com/nkoriyama/aribb24.git | `5e9be272f96e00f15a2f3c5f8ba7e124862aec38` |
| libaribcaption | MIT | https://github.com/xqq/libaribcaption.git | `d59c9d6403645dd7e8098b02b887a6723b15259b` |
| libass | ISC | https://github.com/libass/libass.git | `21b5a54fceff49c126c201d4b51c6445287a5072` |
| libbluray | LGPL-2.1-or-later | https://code.videolan.org/videolan/libbluray.git | `4613535451af402c6d72665e8cd88be1b29c9158` |
| libjxl | BSD-3-Clause and a patent grant | https://github.com/libjxl/libjxl.git | `a14f4e0ff32c07cbd19eae144c850e8bbfee2261` |
| libmp3lame | LGPL-2.0-or-later | https://svn.code.sf.net/p/lame/svn/trunk/lame | `7153` |
| libopus | BSD-3-Clause | https://github.com/xiph/opus.git | `f1afc4222aff7c5456d27581ec618ae64158fbe1` |
| libplacebo | LGPL-2.1-or-later | https://code.videolan.org/videolan/libplacebo.git | `e608e5b799b6515af2689fddbbed66ffae7b724c` |
| mbedtls | Apache-2.0 or GPL-2.0-or-later (used under Apache-2.0) | https://github.com/ARMmbed/mbedtls.git | `v4.3.0` |
| librist | BSD-2-Clause | https://code.videolan.org/rist/librist.git | `a911bee8904d88beb1ccd3349ff9a3de1cadc1a6` |
| glib | LGPL-2.1-or-later | https://github.com/GNOME/glib.git | `5cf924587ed152260d85c6ca8733546bcad5ee42` |
| cairo | LGPL-2.1 or MPL-1.1 | https://gitlab.freedesktop.org/cairo/cairo.git | `07adef44b00a9765e9bdb92c4e6f3772debc83c2` |
| pango | LGPL-2.0-or-later | https://github.com/GNOME/pango.git | `6bd640fdbdfe4d751cc17e0fff25caa6a372d303` |
| librsvg | LGPL-2.1-or-later | https://github.com/GNOME/librsvg.git | `a6ab2529560de5e17551a9ca3ed9e8a9b9bc9a2e` |
| libssh | LGPL-2.1 with OpenSSL exception | https://gitlab.com/libssh/libssh-mirror.git | `5138d46fd3b844d525b05938bf55a6d0d924c8d2` |
| libtheora | BSD-3-Clause | https://github.com/xiph/theora.git | `28fd5ec77f0ad0e07a371cef1047828116f6bd8a` |
| libvpx | BSD-3-Clause and a patent grant | https://chromium.googlesource.com/webm/libvpx | `7e000a41a7727d5b9477dbbe5727431560d6a16c` |
| libwebp | BSD-3-Clause and a patent grant | https://chromium.googlesource.com/webm/libwebp | `67b8354c37d21f36d6d5cdd027ca0bcf94d3243d` |
| libzmq | MPL-2.0 | https://github.com/zeromq/libzmq.git | `46493370217ac135246617fa2f6ac819d8b61bfc` |
| lv2 | ISC | https://github.com/lv2/lv2.git | `3c57dae600a5ad8d05acd53ee3490f7d91cb7be6` |
| serd | ISC | https://github.com/drobilla/serd.git | `b621b0bc790d1e8102d3138ead16b974ddda8432` |
| zix | ISC | https://github.com/drobilla/zix.git | `17d632393f5addb6fa24dda2927b96515407cca7` |
| sord | ISC | https://github.com/drobilla/sord.git | `4b5232bb91e7f2f1b84df384494d073fabbcd312` |
| sratom | ISC | https://github.com/lv2/sratom.git | `9e8ee84eed502e6030ce8bb49694fd28542b08fc` |
| lilv | ISC | https://github.com/lv2/lilv.git | `4b8f30055fa5cb4134a2eb45fc1555bc04289314` |
| onevpl | MIT | https://github.com/intel/libvpl.git | `412c5cffe3022d5ebb8a955c3b30afe7bd2e3929` |
| openal | LGPL-2.0-or-later | https://github.com/kcat/openal-soft.git | `0609c8df5848040a13d67d790ac65ed95fad265f` |
| openapv | BSD-3-Clause | https://github.com/AcademySoftwareFoundation/openapv.git | `3f0291391dbb1fbc27229b2543aad4ce778b0d72` |
| opencore-amr | Apache-2.0 | https://git.code.sf.net/p/opencore-amr/code | `7dba8c32238418ce0b316a852b2224df586ca896` |
| openh264 | BSD-2-Clause | https://github.com/cisco/openh264.git | `1bca62311e447fd9c39fc93de2c4b96cd5ce971c` |
| openjpeg | BSD-2-Clause | https://github.com/uclouvain/openjpeg.git | `8314119b067c0fc77834731168daaebd379fdb12` |
| openmpt | BSD-3-Clause | https://github.com/OpenMPT/openmpt.git | `ad7ba02b3fd1212e65cca02c41476af4368491ce` |
| rav1e | BSD-2-Clause and the Alliance for Open Media patent licence | https://github.com/xiph/rav1e.git | `31435de9d76fddd38f6dcc31d4014574cebb2092` |
| sdl | Zlib (only in ffplay.exe: see below) | https://github.com/libsdl-org/SDL.git | `fe30456e94e89ef2cab50172121bffb7f7f50830` |
| snappy | BSD-3-Clause | https://github.com/google/snappy.git | `946606459314e55d029c85f9a8c46f8809467f0f` |
| soxr | LGPL-2.1-or-later | https://git.code.sf.net/p/soxr/code | `945b592b70470e29f917f4de89b4281fbbd540c0` |
| srt | MPL-2.0 | https://github.com/Haivision/srt.git | `23af65f7dafb0f65b80312b859f80aeece557924` |
| svtav1 | BSD-3-Clause-Clear and the Alliance for Open Media patent licence | https://github.com/AOMediaCodec/SVT-AV1.git | `5fdf6b2f373bd96256bb6b45f892077b1b4431d9` |
| twolame | LGPL-2.1-or-later | https://github.com/njh/twolame.git | `6fced852d4d5cfad58cf9dbe3ea619b08e87d398` |
| uavs3d | BSD-3-Clause | https://github.com/uavs3/uavs3d.git | `0e20d2c291853f196c68922a264bcd8471d75b68` |
| libva | MIT | https://github.com/intel/libva.git | `f052e984e0c8bf10784198cc311b23c567b1367f` |
| vapoursynth | LGPL-2.1-or-later (headers only; loaded at run time if the user has it) | https://github.com/vapoursynth/vapoursynth.git | `R82` |
| vvenc | BSD-3-Clause-Clear (no patent licence) | https://github.com/fraunhoferhhi/vvenc.git | `7804c92e133983e5d81c88d0ae642201d73ee457` |
| zimg | WTFPL | https://github.com/sekrit-twc/zimg.git | `67e0603271c080e22c8429856dd4a8a56587e61e` |

SDL, and libsamplerate through it, are linked only into ffplay.exe, which comes with the build but which Convert Wheel doesn't ship.

Also linked in, through the ones above: KISS FFT (BSD-3-Clause, in Chromaprint), glslang `385445a87d21d7a5c7bee2f7bef8a87850f6bf28`
(several permissive licences) and SPIRV-Tools `45d4e0402a9900af83e97fe8ed6786f4c33bf65c` (Apache-2.0) through shaderc, Highway
(Apache-2.0 or BSD-3-Clause) through libjxl, TF-PSA-Crypto (Apache-2.0 or GPL-2.0-or-later) through Mbed TLS, the Rust crates named in
the `Cargo.lock` of rav1e and librsvg (permissive; their notices come with those sources), and GCC's runtime libraries
(libgcc, libstdc++, libgomp; GPL-3.0 with the GCC Runtime Library Exception, `libraries/gcc-runtime.txt`).
LAME's "commit" is the Subversion revision; its text is `libraries/lame.txt`.
