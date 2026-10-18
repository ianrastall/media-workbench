//! Short-clip MP4 encoding with Windows' own H.264 encoder (Media Foundation).
//!
//! Media Foundation is loaded at run time, never imported: Windows N editions and some servers
//! ship without it, and a missing encoder must refuse one request, not stop the whole core loading.
use crate::path_policy::no_links;
use crate::storage::{Cancel, cancelled};
use crate::{INVALID, IO_ERROR};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    ffi::c_void,
    fs::{self, File},
    io::{BufReader, Read},
    path::{Path, PathBuf},
};

/// Windows has no usable H.264 encoder (for example an N edition without the Media Feature Pack).
pub(crate) const UNSUPPORTED: i32 = 8;
pub(crate) const MAX_FRAMES: u32 = 240;
const MAX_SIDE: u32 = 4096;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Request {
    frames_path: PathBuf,
    width: u32,
    height: u32,
    frames: u32,
    fps: u32,
    destination: PathBuf,
}

struct Partial(PathBuf);
impl Drop for Partial {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

fn validate(input: &[u8]) -> Result<Request, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    let side = |value: u32| (64..=MAX_SIDE).contains(&value) && value.is_multiple_of(2);
    if !side(request.width)
        || !side(request.height)
        || !(1..=MAX_FRAMES).contains(&request.frames)
        || !(1..=60).contains(&request.fps)
        || !request.frames_path.is_absolute()
        || !request.destination.is_absolute()
        || !request
            .destination
            .extension()
            .is_some_and(|ext| ext.eq_ignore_ascii_case("mp4"))
    {
        return Err(INVALID);
    }
    no_links(&request.frames_path)?;
    no_links(&request.destination)?;
    let expected =
        u64::from(request.width) * u64::from(request.height) * 3 * u64::from(request.frames);
    let metadata = fs::symlink_metadata(&request.frames_path).map_err(|_| IO_ERROR)?;
    if !metadata.is_file() || metadata.len() != expected {
        return Err(INVALID);
    }
    let parent = request.destination.parent().ok_or(INVALID)?;
    if !parent.is_dir() || request.destination.exists() {
        return Err(INVALID);
    }
    Ok(request)
}

/// Encodes raw top-down RGB24 frames into an H.264 MP4 at `destination`; returns its SHA-256.
pub(crate) fn encode(input: &[u8], cancel: Cancel, context: *mut c_void) -> Result<String, i32> {
    let request = validate(input)?;
    cancelled(cancel, context)?;
    let name = request
        .destination
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or(INVALID)?;
    let partial = Partial(
        request
            .destination
            .with_file_name(format!(".{name}.{}.partial", std::process::id())),
    );
    let frames = File::open(&request.frames_path).map_err(|_| IO_ERROR)?;
    platform::write(
        &request,
        BufReader::new(frames),
        &partial.0,
        cancel,
        context,
    )?;
    let hash = hash(&partial.0)?;
    if request.destination.exists() {
        return Err(INVALID);
    }
    fs::rename(&partial.0, &request.destination).map_err(|_| IO_ERROR)?;
    std::mem::forget(partial);
    Ok(hash)
}

fn hash(path: &Path) -> Result<String, i32> {
    let mut file = File::open(path).map_err(|_| IO_ERROR)?;
    let mut digest = Sha256::new();
    let mut chunk = vec![0u8; 1 << 16];
    loop {
        match file.read(&mut chunk) {
            Ok(0) => break,
            Ok(count) => digest.update(&chunk[..count]),
            Err(_) => return Err(IO_ERROR),
        }
    }
    let written = fs::metadata(path).map_err(|_| IO_ERROR)?.len();
    // An MP4 starts with its file-type box; anything else means the encoder wrote nothing usable.
    let mut head = [0u8; 8];
    File::open(path)
        .and_then(|mut file| file.read_exact(&mut head))
        .map_err(|_| IO_ERROR)?;
    if written < 64 || &head[4..8] != b"ftyp" {
        return Err(IO_ERROR);
    }
    Ok(format!("{:x}", digest.finalize()))
}

#[cfg(not(windows))]
mod platform {
    use super::*;
    pub(super) fn write(
        _: &Request,
        _: BufReader<File>,
        _: &Path,
        _: Cancel,
        _: *mut c_void,
    ) -> Result<(), i32> {
        Err(UNSUPPORTED)
    }
}

#[cfg(windows)]
mod platform {
    use super::*;
    use std::os::windows::ffi::OsStrExt;
    use windows::Win32::Foundation::RPC_E_CHANGED_MODE;
    use windows::Win32::Media::MediaFoundation::{
        IMFAttributes, IMFMediaBuffer, IMFMediaType, IMFSample, IMFSinkWriter, MF_MT_AVG_BITRATE,
        MF_MT_DEFAULT_STRIDE, MF_MT_FRAME_RATE, MF_MT_FRAME_SIZE, MF_MT_INTERLACE_MODE,
        MF_MT_MAJOR_TYPE, MF_MT_PIXEL_ASPECT_RATIO, MF_MT_SUBTYPE, MF_TRANSCODE_CONTAINERTYPE,
        MFMediaType_Video, MFTranscodeContainerType_MPEG4, MFVideoFormat_H264, MFVideoFormat_RGB32,
        MFVideoInterlace_Progressive,
    };
    use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
    use windows::Win32::System::LibraryLoader::{
        GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW,
    };
    use windows::core::{HRESULT, Interface, PCSTR, PCWSTR};

    /// MF_SDK_VERSION << 16 | MF_API_VERSION, as mfapi.h defines MF_VERSION.
    const MF_VERSION: u32 = 0x0002_0070;
    const MFSTARTUP_FULL: u32 = 0;
    const HUNDRED_NANOSECONDS: i64 = 10_000_000;

    type Startup = unsafe extern "system" fn(u32, u32) -> HRESULT;
    type Shutdown = unsafe extern "system" fn() -> HRESULT;
    type CreateOne = unsafe extern "system" fn(*mut *mut c_void) -> HRESULT;
    type CreateSized = unsafe extern "system" fn(u32, *mut *mut c_void) -> HRESULT;
    type CreateAttributes = unsafe extern "system" fn(*mut *mut c_void, u32) -> HRESULT;
    type CreateWriter =
        unsafe extern "system" fn(PCWSTR, *mut c_void, *mut c_void, *mut *mut c_void) -> HRESULT;

    struct Api {
        startup: Startup,
        shutdown: Shutdown,
        attributes: CreateAttributes,
        media_type: CreateOne,
        sample: CreateOne,
        buffer: CreateSized,
        writer: CreateWriter,
    }

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(Some(0)).collect()
    }

    /// Resolves the few Media Foundation entry points from System32 only; modules stay loaded.
    fn load() -> Result<Api, i32> {
        // SAFETY: names are NUL-terminated; each pointer is transmuted to its documented signature.
        unsafe {
            let library = |name: &str| {
                LoadLibraryExW(
                    PCWSTR(wide(name).as_ptr()),
                    None,
                    LOAD_LIBRARY_SEARCH_SYSTEM32,
                )
                .map_err(|_| UNSUPPORTED)
            };
            let platform = library("mfplat.dll")?;
            let readwrite = library("mfreadwrite.dll")?;
            macro_rules! find {
                ($module:expr, $name:literal, $kind:ty) => {
                    std::mem::transmute::<unsafe extern "system" fn() -> isize, $kind>(
                        GetProcAddress($module, PCSTR(concat!($name, "\0").as_ptr()))
                            .ok_or(UNSUPPORTED)?,
                    )
                };
            }
            Ok(Api {
                startup: find!(platform, "MFStartup", Startup),
                shutdown: find!(platform, "MFShutdown", Shutdown),
                attributes: find!(platform, "MFCreateAttributes", CreateAttributes),
                media_type: find!(platform, "MFCreateMediaType", CreateOne),
                sample: find!(platform, "MFCreateSample", CreateOne),
                buffer: find!(platform, "MFCreateMemoryBuffer", CreateSized),
                writer: find!(readwrite, "MFCreateSinkWriterFromURL", CreateWriter),
            })
        }
    }

    /// Takes ownership of an interface pointer an MF factory returned.
    unsafe fn owned<T: Interface>(code: HRESULT, raw: *mut c_void, failure: i32) -> Result<T, i32> {
        if code.is_err() || raw.is_null() {
            return Err(failure);
        }
        // SAFETY: the factory succeeded and transferred one reference to the caller.
        Ok(unsafe { T::from_raw(raw) })
    }

    fn video_type(
        api: &Api,
        subtype: &windows::core::GUID,
        request: &Request,
    ) -> Result<IMFMediaType, i32> {
        let mut raw = std::ptr::null_mut();
        // SAFETY: documented MF factory and attribute setters on a live media type.
        unsafe {
            let kind: IMFMediaType = owned((api.media_type)(&mut raw), raw, UNSUPPORTED)?;
            let packed = |high: u32, low: u32| (u64::from(high) << 32) | u64::from(low);
            kind.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)
                .and_then(|_| kind.SetGUID(&MF_MT_SUBTYPE, subtype))
                .and_then(|_| {
                    kind.SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)
                })
                .and_then(|_| {
                    kind.SetUINT64(&MF_MT_FRAME_SIZE, packed(request.width, request.height))
                })
                .and_then(|_| kind.SetUINT64(&MF_MT_FRAME_RATE, packed(request.fps, 1)))
                .and_then(|_| kind.SetUINT64(&MF_MT_PIXEL_ASPECT_RATIO, packed(1, 1)))
                .map_err(|_| UNSUPPORTED)?;
            Ok(kind)
        }
    }

    struct Session {
        api: Api,
        uninitialize: bool,
    }
    impl Drop for Session {
        fn drop(&mut self) {
            // SAFETY: balances the successful MFStartup/CoInitializeEx in `start`.
            unsafe {
                let _ = (self.api.shutdown)();
                if self.uninitialize {
                    CoUninitialize();
                }
            }
        }
    }

    fn start() -> Result<Session, i32> {
        let api = load()?;
        // SAFETY: COM/MF initialization for this thread, balanced by Session::drop.
        unsafe {
            let com = CoInitializeEx(None, COINIT_MULTITHREADED);
            if com.is_err() && com != RPC_E_CHANGED_MODE {
                return Err(UNSUPPORTED);
            }
            let uninitialize = com.is_ok();
            if (api.startup)(MF_VERSION, MFSTARTUP_FULL).is_err() {
                if uninitialize {
                    CoUninitialize();
                }
                return Err(UNSUPPORTED);
            }
            Ok(Session { api, uninitialize })
        }
    }

    pub(super) fn write(
        request: &Request,
        mut frames: BufReader<File>,
        output: &Path,
        cancel: Cancel,
        context: *mut c_void,
    ) -> Result<(), i32> {
        let session = start()?;
        let api = &session.api;
        let path: Vec<u16> = output.as_os_str().encode_wide().chain(Some(0)).collect();
        let bitrate =
            (u64::from(request.width) * u64::from(request.height) * u64::from(request.fps) * 3 / 2)
                .clamp(1_000_000, 20_000_000) as u32;
        let stride = request.width * 4;
        let length = stride * request.height;
        let duration = HUNDRED_NANOSECONDS / i64::from(request.fps);
        // SAFETY: every interface comes from a successful MF factory and is used on this thread only.
        unsafe {
            let mut raw = std::ptr::null_mut();
            let attributes: IMFAttributes = owned((api.attributes)(&mut raw, 1), raw, UNSUPPORTED)?;
            // The container comes from this attribute, so the partial file needs no .mp4 name.
            attributes
                .SetGUID(&MF_TRANSCODE_CONTAINERTYPE, &MFTranscodeContainerType_MPEG4)
                .map_err(|_| UNSUPPORTED)?;
            let mut raw = std::ptr::null_mut();
            let writer: IMFSinkWriter = owned(
                (api.writer)(
                    PCWSTR(path.as_ptr()),
                    std::ptr::null_mut(),
                    attributes.as_raw(),
                    &mut raw,
                ),
                raw,
                UNSUPPORTED,
            )?;
            let encoded = video_type(api, &MFVideoFormat_H264, request)?;
            encoded
                .SetUINT32(&MF_MT_AVG_BITRATE, bitrate)
                .map_err(|_| UNSUPPORTED)?;
            let stream = writer.AddStream(&encoded).map_err(|_| UNSUPPORTED)?;
            let source = video_type(api, &MFVideoFormat_RGB32, request)?;
            // A positive stride means top-down rows, the order the frames file uses.
            source
                .SetUINT32(&MF_MT_DEFAULT_STRIDE, stride)
                .map_err(|_| UNSUPPORTED)?;
            writer
                .SetInputMediaType(stream, &source, None)
                .map_err(|_| UNSUPPORTED)?;
            writer.BeginWriting().map_err(|_| UNSUPPORTED)?;
            let mut row = vec![0u8; request.width as usize * 3];
            for index in 0..request.frames {
                cancelled(cancel, context)?;
                let mut raw = std::ptr::null_mut();
                let buffer: IMFMediaBuffer = owned((api.buffer)(length, &mut raw), raw, IO_ERROR)?;
                let mut data = std::ptr::null_mut();
                buffer.Lock(&mut data, None, None).map_err(|_| IO_ERROR)?;
                let pixels = std::slice::from_raw_parts_mut(data, length as usize);
                let mut filled = Ok(());
                for line in pixels.chunks_exact_mut(stride as usize) {
                    if let Err(error) = frames.read_exact(&mut row) {
                        filled = Err(error);
                        break;
                    }
                    for (target, rgb) in line.chunks_exact_mut(4).zip(row.chunks_exact(3)) {
                        target.copy_from_slice(&[rgb[2], rgb[1], rgb[0], 255]);
                    }
                }
                buffer.Unlock().map_err(|_| IO_ERROR)?;
                filled.map_err(|_| IO_ERROR)?;
                buffer.SetCurrentLength(length).map_err(|_| IO_ERROR)?;
                let mut raw = std::ptr::null_mut();
                let sample: IMFSample = owned((api.sample)(&mut raw), raw, IO_ERROR)?;
                sample.AddBuffer(&buffer).map_err(|_| IO_ERROR)?;
                sample
                    .SetSampleTime(i64::from(index) * duration)
                    .map_err(|_| IO_ERROR)?;
                sample.SetSampleDuration(duration).map_err(|_| IO_ERROR)?;
                writer.WriteSample(stream, &sample).map_err(|_| IO_ERROR)?;
            }
            writer.Finalize().map_err(|_| IO_ERROR)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frames(folder: &Path, width: u32, height: u32, count: u32) -> PathBuf {
        let mut bytes = Vec::new();
        for frame in 0..count {
            for y in 0..height {
                for x in 0..width {
                    bytes.extend_from_slice(&[(x * 4) as u8, (y * 4) as u8, (frame * 30) as u8]);
                }
            }
        }
        let path = folder.join("frames.rgb");
        fs::write(&path, bytes).unwrap();
        path
    }

    fn request(frames: &Path, destination: &Path, width: u32, count: u32) -> Vec<u8> {
        serde_json::to_vec(&serde_json::json!({
            "framesPath": frames, "width": width, "height": 64, "frames": count, "fps": 8,
            "destination": destination,
        }))
        .unwrap()
    }

    fn folder(name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "media-workbench-video-{name}-{}",
            std::process::id()
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).unwrap();
        path
    }

    #[test]
    fn encodes_frames_into_an_mp4_or_says_the_encoder_is_missing() {
        let folder = folder("encode");
        let source = frames(&folder, 96, 64, 8);
        let destination = folder.join("video.mp4");
        match encode(
            &request(&source, &destination, 96, 8),
            None,
            std::ptr::null_mut(),
        ) {
            Ok(hash) => {
                eprintln!("Media Foundation encoded the test clip");
                let bytes = fs::read(&destination).unwrap();
                assert_eq!(&bytes[4..8], b"ftyp");
                assert_eq!(hash, format!("{:x}", Sha256::digest(&bytes)));
            }
            // Windows editions without Media Foundation refuse plainly instead of failing to load.
            Err(code) => {
                eprintln!("Media Foundation is unavailable here ({code})");
                assert_eq!(code, UNSUPPORTED);
            }
        }
        let leftovers = fs::read_dir(&folder)
            .unwrap()
            .filter_map(Result::ok)
            .filter(|entry| entry.file_name().to_string_lossy().ends_with(".partial"))
            .count();
        assert_eq!(leftovers, 0);
        fs::remove_dir_all(folder).unwrap();
    }

    #[test]
    fn refuses_bad_requests_before_writing() {
        let folder = folder("refuse");
        let source = frames(&folder, 64, 64, 2);
        let destination = folder.join("video.mp4");
        let wrong_count = request(&source, &destination, 64, 3);
        let odd = request(&source, &destination, 63, 2);
        let not_mp4 = request(&source, &folder.join("video.gif"), 64, 2);
        for bad in [wrong_count, odd, not_mp4, b"{}".to_vec()] {
            assert_eq!(encode(&bad, None, std::ptr::null_mut()), Err(INVALID));
        }
        fs::write(&destination, b"kept").unwrap();
        assert_eq!(
            encode(
                &request(&source, &destination, 64, 2),
                None,
                std::ptr::null_mut()
            ),
            Err(INVALID)
        );
        assert_eq!(fs::read(&destination).unwrap(), b"kept");
        fs::remove_dir_all(folder).unwrap();
    }

    #[test]
    fn cancellation_leaves_no_partial_video() {
        unsafe extern "C" fn stop(_: *mut c_void) -> i32 {
            1
        }
        let folder = folder("cancel");
        let source = frames(&folder, 64, 64, 4);
        let destination = folder.join("video.mp4");
        assert_eq!(
            encode(
                &request(&source, &destination, 64, 4),
                Some(stop),
                std::ptr::null_mut()
            ),
            Err(crate::artifact::CANCELLED)
        );
        assert!(!destination.exists());
        fs::remove_dir_all(folder).unwrap();
    }
}
