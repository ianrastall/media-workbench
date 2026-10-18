//! Metadata is only a download plan; header, file, runtime and GPU checks still decide readiness.
use crate::{INVALID, valid_relative_path};
use serde::{Deserialize, Serialize};

pub(crate) const OUTPUT_CAPACITY: usize = 65_536;
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Request {
    repo_id: String,
    revision: String,
    license: String,
    gated: bool,
    files: Vec<Weight>,
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
struct Weight {
    path: String,
    bytes: u64,
    sha256: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Plan {
    files: Vec<Weight>,
    blocker: &'static str,
}

fn hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
}

fn is_shard(path: &str) -> bool {
    let stem = path.strip_suffix(".safetensors").unwrap_or(path);
    let Some((prefix, total)) = stem.rsplit_once("-of-") else {
        return false;
    };
    let part = prefix.rsplit('-').next().unwrap_or("");
    !part.is_empty()
        && part.bytes().all(|c| c.is_ascii_digit())
        && !total.is_empty()
        && total.bytes().all(|c| c.is_ascii_digit())
}

pub(crate) fn plan(input: &[u8]) -> Result<Vec<u8>, i32> {
    let request: Request = serde_json::from_slice(input).map_err(|_| INVALID)?;
    let parts: Vec<_> = request.repo_id.split('/').collect();
    if parts.len() != 2
        || parts.iter().any(|p| {
            p.is_empty()
                || p.len() > 96
                || !valid_relative_path(p)
                || !p
                    .bytes()
                    .all(|c| c.is_ascii_alphanumeric() || b"_.-".contains(&c))
        })
        || request.files.len() > 512
        || request.license.len() > 1024
    {
        return Err(INVALID);
    }
    let mut files = Vec::new();
    for file in request.files {
        // A Diffusers component or sharded part is not a complete single-file checkpoint.
        if !file.path.contains('/')
            && file.path.len() <= 160
            && valid_relative_path(&file.path)
            && file.path.ends_with(".safetensors")
            && !is_shard(&file.path)
            && (9..=32 * 1024_u64.pow(3)).contains(&file.bytes)
            && hex(&file.sha256, 64)
        {
            if files.iter().any(|f: &Weight| f.path == file.path) {
                return Err(INVALID);
            }
            files.push(file);
        }
    }
    files.sort_by(|a, b| a.bytes.cmp(&b.bytes).then(a.path.cmp(&b.path)));
    if files.len() > 64 {
        return Err(INVALID);
    }
    let blocker = if request.gated {
        "This model requires account access. Protected downloads are not supported yet."
    } else if !hex(&request.revision, 40) {
        "The Hub did not report a pinned model revision. Refresh the model details."
    } else if request.license.trim().is_empty() || request.license == "Not specified" {
        "The publisher has not specified a license. Review the source before downloading a model yourself."
    } else if files.is_empty() {
        "No supported single-file model download is available here. You can still add a Stable Diffusion 1.5 model you downloaded in Create."
    } else {
        ""
    };
    serde_json::to_vec(&Plan { files, blocker }).map_err(|_| crate::INTERNAL)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn request() -> serde_json::Value {
        serde_json::json!({
            "repoId":"owner/model", "revision":"a".repeat(40), "license":"openrail", "gated":false,
            "files":[{"path":"model.safetensors","bytes":100,"sha256":"b".repeat(64)}]
        })
    }
    fn run(value: &serde_json::Value) -> serde_json::Value {
        serde_json::from_slice(&plan(&serde_json::to_vec(value).unwrap()).unwrap()).unwrap()
    }
    #[test]
    fn metadata_never_grants_readiness_and_filters_unsafe_or_partial_files() {
        let mut value = request();
        assert_eq!(run(&value)["blocker"], "");
        assert!(run(&value).get("ready").is_none());
        value["files"][0]["path"] = "dream-of-spring.safetensors".into();
        assert_eq!(run(&value)["files"].as_array().unwrap().len(), 1);
        for path in [
            "../model.safetensors",
            "unet/model.safetensors",
            "NUL.safetensors",
            "part-00001-of-00002.safetensors",
            "model.ckpt",
        ] {
            value["files"][0]["path"] = path.into();
            assert!(
                run(&value)["files"].as_array().unwrap().is_empty(),
                "{path}"
            );
        }
    }
    #[test]
    fn requires_public_access_license_and_pinned_revision_and_hash() {
        for field in ["license", "revision"] {
            let mut value = request();
            value[field] = "".into();
            assert_ne!(run(&value)["blocker"], "");
        }
        let mut value = request();
        value["gated"] = true.into();
        assert_ne!(run(&value)["blocker"], "");
        value = request();
        value["files"][0]["sha256"] = "missing".into();
        assert_ne!(run(&value)["blocker"], "");
        value = request();
        value["repoId"] = "../outside".into();
        assert_eq!(plan(&serde_json::to_vec(&value).unwrap()), Err(INVALID));
    }
}
