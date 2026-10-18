//! Pure SD 1.5 and SDXL tensor-layout policy. Never reads weights, executes model code or grants readiness.
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

pub const MAX_INPUT: usize = 100 * 1024 * 1024;
pub const OUTPUT_CAPACITY: usize = 1024;
const NOT_SD15: &str = "Media Workbench can add Stable Diffusion 1.5 and Stable Diffusion XL models for now, such as fine-tuned checkpoints of either.";
const NOT_SDXL: &str =
    "Choose an SDXL base model or a fine-tuned version of it as a single .safetensors file.";
const UNKNOWN: &str = "This file is not a Stable Diffusion model that Media Workbench recognizes. ";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
enum Kind {
    Checkpoint,
    Unet,
    TextEncoder,
    Vae,
}

#[derive(Deserialize)]
struct Tensor {
    dtype: String,
    shape: Vec<u64>,
}

#[derive(Deserialize)]
struct Request {
    kind: Kind,
    tensors: BTreeMap<String, Tensor>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    compatible: bool,
    own_vae: bool,
    /// "sd15" or "sdxl" when compatible; empty otherwise.
    family: &'static str,
    message: String,
}

type Tensors = BTreeMap<String, Tensor>;
type Decision = Result<bool, String>;
type Identified = Result<(bool, &'static str), String>;

pub fn inspect(input: &[u8]) -> Result<Vec<u8>, i32> {
    if input.len() > MAX_INPUT {
        return Err(super::INVALID);
    }
    let request: Request = serde_json::from_slice(input).map_err(|_| super::INVALID)?;
    if request.tensors.is_empty()
        || request.tensors.len() > 32000
        || request
            .tensors
            .values()
            .any(|tensor| tensor.shape.len() > 8 || tensor.shape.iter().any(|&side| side > 1 << 31))
    {
        return Err(super::INVALID);
    }
    let sd15 = |decision: Decision| decision.map(|own_vae| (own_vae, "sd15"));
    let decision = match request.kind {
        Kind::Checkpoint => checkpoint(&request.tensors),
        Kind::Unet => sd15(unet(&request.tensors)),
        Kind::TextEncoder => sd15(text_encoder(&request.tensors)),
        Kind::Vae => sd15(vae(&request.tensors)),
    };
    let report = match decision {
        Ok((own_vae, family)) => Report {
            compatible: true,
            own_vae,
            family,
            message: String::new(),
        },
        Err(message) => Report {
            compatible: false,
            own_vae: false,
            family: "",
            message,
        },
    };
    serde_json::to_vec(&report).map_err(|_| super::INTERNAL)
}

fn shape<'a>(tensors: &'a Tensors, name: &str) -> Option<&'a [u64]> {
    tensors.get(name).map(|tensor| tensor.shape.as_slice())
}

fn fragments(tensors: &Tensors, parts: &[&str]) -> bool {
    tensors
        .keys()
        .any(|name| parts.iter().any(|part| name.contains(part)))
}

/// LoRAs, embeddings and ControlNets, which are add-ons for any family rather than complete models.
fn refuse_add_ons(tensors: &Tensors) -> Result<(), String> {
    let reason = if fragments(
        tensors,
        &[
            "lora_up",
            "lora_down",
            "lora_A",
            "lora_B",
            "lora_unet_",
            "lora_te",
            ".lora.",
        ],
    ) {
        "This is a LoRA add-on that adjusts another model's style, not a complete model. Adding LoRAs is not supported yet.".into()
    } else if ["emb_params", "clip_l", "clip_g"]
        .iter()
        .any(|name| tensors.contains_key(*name))
        || fragments(tensors, &["string_to_param"])
    {
        "This is a textual-inversion embedding (a small style add-on), not a complete model.".into()
    } else if fragments(
        tensors,
        &[
            "control_model.",
            "input_hint_block",
            "controlnet_cond_embedding",
        ],
    ) {
        "This is a ControlNet add-on that guides another model, not a complete model.".into()
    } else {
        return Ok(());
    };
    Err(reason)
}

/// Other model families, after the add-ons; Stable Diffusion XL here means a part of one (a full SDXL
/// checkpoint is recognized before this runs).
fn refuse_other_kinds(tensors: &Tensors) -> Result<(), String> {
    refuse_add_ons(tensors)?;
    let reason = if fragments(
        tensors,
        &[
            "conditioner.embedders.",
            "label_emb.0.0.weight",
            "add_embedding.linear_1",
        ],
    ) {
        format!("This is part of a Stable Diffusion XL model. {NOT_SDXL}")
    } else if fragments(tensors, &["double_blocks.", "single_transformer_blocks."]) {
        format!("This is a FLUX model. {NOT_SD15}")
    } else if fragments(
        tensors,
        &["joint_blocks.", "transformer_blocks.0.attn.add_k_proj"],
    ) {
        format!("This is a Stable Diffusion 3 model. {NOT_SD15}")
    } else if fragments(
        tensors,
        &[
            "cond_stage_model.model.token_embedding",
            "cond_stage_model.model.transformer.",
        ],
    ) {
        format!("This is a Stable Diffusion 2 model. {NOT_SD15}")
    } else {
        return Ok(());
    };
    Err(reason)
}

fn check_unet(
    input: Option<&[u64]>,
    cross: Option<&[u64]>,
    output: Option<&[u64]>,
) -> Result<(), String> {
    let (Some(input), Some(cross), Some(output)) = (input, cross, output) else {
        return Err(format!("{UNKNOWN}{NOT_SD15}"));
    };
    if input == [320, 9, 3, 3] {
        return Err("This is a dedicated inpainting model. Painted changes already work with ordinary Stable Diffusion 1.5 models; dedicated inpainting models are not supported yet.".into());
    }
    if input == [320, 8, 3, 3] {
        return Err(
            "This is an instruction-editing (InstructPix2Pix) model, which is not supported yet."
                .into(),
        );
    }
    if cross == [320, 1024] {
        return Err(format!("This is a Stable Diffusion 2 model. {NOT_SD15}"));
    }
    if input != [320, 4, 3, 3] || output != [4, 320, 3, 3] || cross != [320, 768] {
        return Err(format!(
            "This model's layout does not match Stable Diffusion 1.5. {NOT_SD15}"
        ));
    }
    Ok(())
}

fn check_text(embedding: Option<&[u64]>) -> Result<(), String> {
    match embedding {
        None => Err("This model has no text encoder, so it cannot read a description. Choose a complete model.".into()),
        Some([49408, 1024]) => Err(format!("This is a Stable Diffusion 2 model. {NOT_SD15}")),
        Some([49408, 768]) => Ok(()),
        _ => Err(format!("This model's text encoder does not match Stable Diffusion 1.5. {NOT_SD15}")),
    }
}

fn check_vae(decoder: Option<&[u64]>, encoder: Option<&[u64]>) -> Result<(), String> {
    if decoder != Some([512, 4, 3, 3].as_slice()) || encoder != Some([8, 512, 3, 3].as_slice()) {
        return Err(
            "This model's picture decoder (VAE) does not match Stable Diffusion 1.5.".into(),
        );
    }
    Ok(())
}

fn check_types(tensors: &Tensors, prefixes: &[&str]) -> Result<(), String> {
    if tensors.iter().any(|(name, tensor)| {
        prefixes.iter().any(|prefix| name.starts_with(prefix))
            && !matches!(
                tensor.dtype.as_str(),
                "F32" | "F16" | "BF16" | "I64" | "I32"
            )
    }) {
        return Err("This model stores its weights in a compressed number format that is not supported. Choose a 16- or 32-bit version of it.".into());
    }
    Ok(())
}

/// A single-file Stable Diffusion XL checkpoint: the base layout's two text encoders and UNet.
fn is_sdxl(tensors: &Tensors) -> bool {
    tensors
        .keys()
        .any(|name| name.starts_with("conditioner.embedders."))
        && tensors
            .keys()
            .any(|name| name.starts_with("model.diffusion_model."))
}

fn sdxl_checkpoint(tensors: &Tensors) -> Decision {
    let unet = "model.diffusion_model.";
    let input = shape(tensors, "model.diffusion_model.input_blocks.0.0.weight");
    let label = shape(tensors, "model.diffusion_model.label_emb.0.0.weight");
    if input == Some([384, 4, 3, 3].as_slice()) || label == Some([1536, 2560].as_slice()) {
        return Err(format!(
            "This is the Stable Diffusion XL refiner, which only finishes another model's pictures. {NOT_SDXL}"
        ));
    }
    if input == Some([320, 9, 3, 3].as_slice()) {
        return Err("This is a dedicated Stable Diffusion XL inpainting model. Painted changes already work with ordinary SDXL models; dedicated inpainting models are not supported yet.".into());
    }
    let expected = [
        (
            "model.diffusion_model.input_blocks.0.0.weight",
            &[320, 4, 3, 3][..],
        ),
        ("model.diffusion_model.out.2.weight", &[4, 320, 3, 3][..]),
        (
            "model.diffusion_model.label_emb.0.0.weight",
            &[1280, 2816][..],
        ),
        (
            "model.diffusion_model.input_blocks.4.1.transformer_blocks.0.attn2.to_k.weight",
            &[640, 2048][..],
        ),
        (
            "conditioner.embedders.0.transformer.text_model.embeddings.token_embedding.weight",
            &[49408, 768][..],
        ),
        (
            "conditioner.embedders.1.model.token_embedding.weight",
            &[49408, 1280][..],
        ),
    ];
    if expected
        .iter()
        .any(|(name, size)| shape(tensors, name) != Some(*size))
    {
        return Err(format!(
            "This model's layout does not match Stable Diffusion XL. {NOT_SDXL}"
        ));
    }
    let own_vae = tensors
        .keys()
        .any(|name| name.starts_with("first_stage_model."));
    if own_vae {
        // SDXL's picture decoder has the same layout as Stable Diffusion 1.5's.
        check_vae(
            shape(tensors, "first_stage_model.decoder.conv_in.weight"),
            shape(tensors, "first_stage_model.encoder.conv_out.weight"),
        )?;
    }
    check_types(tensors, &[unet, "conditioner.", "first_stage_model."])?;
    Ok(own_vae)
}

fn checkpoint(tensors: &Tensors) -> Identified {
    refuse_add_ons(tensors)?;
    if is_sdxl(tensors) {
        return sdxl_checkpoint(tensors).map(|own_vae| (own_vae, "sdxl"));
    }
    refuse_other_kinds(tensors)?;
    let unet = "model.diffusion_model.";
    if !tensors.keys().any(|name| name.starts_with(unet)) {
        return Err(
            if tensors.contains_key("conv_in.weight")
                && tensors.keys().any(|name| name.starts_with("down_blocks."))
            {
                "This file is only one part of a model (its UNet). Choose the model's whole folder, or a single-file checkpoint.".into()
            } else if tensors.contains_key("encoder.conv_in.weight")
                && tensors.contains_key("decoder.conv_in.weight")
            {
                "This is only a picture decoder (VAE), not a complete model.".into()
            } else if tensors.keys().any(|name| name.starts_with("text_model.")) {
                "This is only a text encoder, not a complete model.".into()
            } else {
                format!("{UNKNOWN}{NOT_SD15}")
            },
        );
    }
    check_unet(
        shape(tensors, "model.diffusion_model.input_blocks.0.0.weight"),
        shape(
            tensors,
            "model.diffusion_model.input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight",
        ),
        shape(tensors, "model.diffusion_model.out.2.weight"),
    )?;
    check_text(
        shape(
            tensors,
            "cond_stage_model.transformer.text_model.embeddings.token_embedding.weight",
        )
        .or_else(|| {
            shape(
                tensors,
                "cond_stage_model.transformer.embeddings.token_embedding.weight",
            )
        }),
    )?;
    let own_vae = tensors
        .keys()
        .any(|name| name.starts_with("first_stage_model."));
    if own_vae {
        check_vae(
            shape(tensors, "first_stage_model.decoder.conv_in.weight"),
            shape(tensors, "first_stage_model.encoder.conv_out.weight"),
        )?;
    }
    check_types(tensors, &[unet, "cond_stage_model.", "first_stage_model."])?;
    Ok((own_vae, "sd15"))
}

fn unet(tensors: &Tensors) -> Decision {
    refuse_other_kinds(tensors)?;
    check_unet(
        shape(tensors, "conv_in.weight"),
        shape(
            tensors,
            "down_blocks.0.attentions.0.transformer_blocks.0.attn2.to_k.weight",
        ),
        shape(tensors, "conv_out.weight"),
    )?;
    check_types(tensors, &[""])?;
    Ok(false)
}

fn text_encoder(tensors: &Tensors) -> Decision {
    check_text(shape(
        tensors,
        "text_model.embeddings.token_embedding.weight",
    ))?;
    check_types(tensors, &["text_model."])?;
    Ok(false)
}

fn vae(tensors: &Tensors) -> Decision {
    check_vae(
        shape(tensors, "decoder.conv_in.weight"),
        shape(tensors, "encoder.conv_out.weight"),
    )?;
    check_types(tensors, &[""])?;
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::{Value, json};

    fn checkpoint_request() -> Value {
        json!({"kind":"checkpoint", "tensors":{
            "model.diffusion_model.input_blocks.0.0.weight":{"dtype":"F16","shape":[320,4,3,3]},
            "model.diffusion_model.input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight":{"dtype":"F16","shape":[320,768]},
            "model.diffusion_model.out.2.weight":{"dtype":"F16","shape":[4,320,3,3]},
            "cond_stage_model.transformer.text_model.embeddings.token_embedding.weight":{"dtype":"F16","shape":[49408,768]},
            "cond_stage_model.transformer.text_model.embeddings.position_ids":{"dtype":"I64","shape":[1,77]}
        }})
    }

    fn report(request: &Value) -> Value {
        serde_json::from_slice(&inspect(&serde_json::to_vec(request).unwrap()).unwrap()).unwrap()
    }

    #[test]
    fn complete_checkpoint_uses_own_or_bundled_vae_and_alternate_clip_key() {
        let mut request = checkpoint_request();
        assert_eq!(
            report(&request),
            json!({"compatible":true,"ownVae":false,"family":"sd15","message":""})
        );
        let tensors = request["tensors"].as_object_mut().unwrap();
        let text = tensors
            .remove("cond_stage_model.transformer.text_model.embeddings.token_embedding.weight")
            .unwrap();
        tensors.insert(
            "cond_stage_model.transformer.embeddings.token_embedding.weight".into(),
            text,
        );
        tensors.insert(
            "first_stage_model.decoder.conv_in.weight".into(),
            json!({"dtype":"F32","shape":[512,4,3,3]}),
        );
        tensors.insert(
            "first_stage_model.encoder.conv_out.weight".into(),
            json!({"dtype":"BF16","shape":[8,512,3,3]}),
        );
        assert_eq!(report(&request)["ownVae"], true);
        request["tensors"]["first_stage_model.encoder.conv_out.weight"]["shape"] =
            json!([4, 512, 3, 3]);
        assert!(
            report(&request)["message"]
                .as_str()
                .unwrap()
                .contains("picture decoder")
        );
    }

    fn sdxl_request() -> Value {
        json!({"kind":"checkpoint", "tensors":{
            "model.diffusion_model.input_blocks.0.0.weight":{"dtype":"F16","shape":[320,4,3,3]},
            "model.diffusion_model.out.2.weight":{"dtype":"F16","shape":[4,320,3,3]},
            "model.diffusion_model.label_emb.0.0.weight":{"dtype":"F16","shape":[1280,2816]},
            "model.diffusion_model.input_blocks.4.1.transformer_blocks.0.attn2.to_k.weight":{"dtype":"F16","shape":[640,2048]},
            "conditioner.embedders.0.transformer.text_model.embeddings.token_embedding.weight":{"dtype":"F16","shape":[49408,768]},
            "conditioner.embedders.1.model.token_embedding.weight":{"dtype":"F16","shape":[49408,1280]},
            "first_stage_model.decoder.conv_in.weight":{"dtype":"F16","shape":[512,4,3,3]},
            "first_stage_model.encoder.conv_out.weight":{"dtype":"F16","shape":[8,512,3,3]}
        }})
    }

    #[test]
    fn sdxl_checkpoints_are_recognized_and_refiners_and_part_layouts_refused() {
        // Shapes as in the official sd_xl_base_1.0.safetensors header.
        let request = sdxl_request();
        assert_eq!(
            report(&request),
            json!({"compatible":true,"ownVae":true,"family":"sdxl","message":""})
        );
        let mut without_vae = sdxl_request();
        let tensors = without_vae["tensors"].as_object_mut().unwrap();
        tensors.retain(|name, _| !name.starts_with("first_stage_model."));
        assert_eq!(report(&without_vae)["ownVae"], false);
        // As in sd_xl_refiner_1.0.safetensors: wider input, other label embedding, one text encoder.
        let mut refiner = sdxl_request();
        refiner["tensors"]["model.diffusion_model.input_blocks.0.0.weight"]["shape"] =
            json!([384, 4, 3, 3]);
        refiner["tensors"]["model.diffusion_model.label_emb.0.0.weight"]["shape"] =
            json!([1536, 2560]);
        assert!(
            report(&refiner)["message"]
                .as_str()
                .unwrap()
                .contains("refiner")
        );
        let mut inpainting = sdxl_request();
        inpainting["tensors"]["model.diffusion_model.input_blocks.0.0.weight"]["shape"] =
            json!([320, 9, 3, 3]);
        assert!(
            report(&inpainting)["message"]
                .as_str()
                .unwrap()
                .contains("inpainting")
        );
        let mut other = sdxl_request();
        other["tensors"]["conditioner.embedders.1.model.token_embedding.weight"]["shape"] =
            json!([49408, 1024]);
        let refused = report(&other);
        assert_eq!(
            (refused["compatible"].clone(), refused["family"].clone()),
            (json!(false), json!(""))
        );
        assert!(
            refused["message"]
                .as_str()
                .unwrap()
                .contains("does not match Stable Diffusion XL")
        );
        let mut lora = sdxl_request();
        lora["tensors"]["lora_unet_a.lora_up.weight"] = json!({"dtype":"F16","shape":[1]});
        assert!(report(&lora)["message"].as_str().unwrap().contains("LoRA"));
        let mut compressed = sdxl_request();
        compressed["tensors"]["model.diffusion_model.out.2.weight"]["dtype"] = json!("F8_E4M3");
        assert!(
            report(&compressed)["message"]
                .as_str()
                .unwrap()
                .contains("compressed")
        );
        // Diffusers parts of an SDXL model are not added as SD 1.5 parts.
        let unet = json!({"kind":"unet","tensors":{
            "conv_in.weight":{"dtype":"F16","shape":[320,4,3,3]},
            "conv_out.weight":{"dtype":"F16","shape":[4,320,3,3]},
            "add_embedding.linear_1.weight":{"dtype":"F16","shape":[1280,2816]},
            "down_blocks.0.attentions.0.transformer_blocks.0.attn2.to_k.weight":{"dtype":"F16","shape":[320,2048]}
        }});
        assert!(
            report(&unet)["message"]
                .as_str()
                .unwrap()
                .contains("Stable Diffusion XL")
        );
    }

    #[test]
    fn recognizable_families_and_addons_keep_plain_refusals() {
        for (name, reason) in [
            ("lora_unet_a.lora_up.weight", "LoRA"),
            ("clip_l", "textual-inversion"),
            ("conditioner.embedders.0.weight", "Stable Diffusion XL"),
            ("double_blocks.0.weight", "FLUX"),
            ("joint_blocks.0.weight", "Stable Diffusion 3"),
            ("control_model.weight", "ControlNet"),
            (
                "cond_stage_model.model.token_embedding.weight",
                "Stable Diffusion 2",
            ),
        ] {
            let mut request = checkpoint_request();
            request["tensors"][name] = json!({"dtype":"F16","shape":[1]});
            let result = report(&request);
            assert_eq!(result["compatible"], false);
            assert_eq!(result["ownVae"], false);
            assert!(result["message"].as_str().unwrap().contains(reason));
        }
        for (shape, reason) in [
            (json!([320, 9, 3, 3]), "inpainting"),
            (json!([320, 8, 3, 3]), "InstructPix2Pix"),
        ] {
            let mut request = checkpoint_request();
            request["tensors"]["model.diffusion_model.input_blocks.0.0.weight"]["shape"] = shape;
            assert!(
                report(&request)["message"]
                    .as_str()
                    .unwrap()
                    .contains(reason)
            );
        }
        let mut request = checkpoint_request();
        request["tensors"]["model.diffusion_model.out.2.weight"]["dtype"] = json!("F8_E4M3");
        assert!(
            report(&request)["message"]
                .as_str()
                .unwrap()
                .contains("compressed number format")
        );
    }

    #[test]
    fn diffusers_parts_are_checked_independently_but_are_not_checkpoints() {
        let requests = [
            json!({"kind":"unet","tensors":{
                "conv_in.weight":{"dtype":"F16","shape":[320,4,3,3]},
                "conv_out.weight":{"dtype":"F16","shape":[4,320,3,3]},
                "down_blocks.0.attentions.0.transformer_blocks.0.attn2.to_k.weight":{"dtype":"F16","shape":[320,768]}
            }}),
            json!({"kind":"textEncoder","tensors":{
                "text_model.embeddings.token_embedding.weight":{"dtype":"F16","shape":[49408,768]}
            }}),
            json!({"kind":"vae","tensors":{
                "encoder.conv_in.weight":{"dtype":"F16","shape":[128,3,3,3]},
                "decoder.conv_in.weight":{"dtype":"F16","shape":[512,4,3,3]},
                "encoder.conv_out.weight":{"dtype":"F16","shape":[8,512,3,3]}
            }}),
        ];
        for mut request in requests {
            assert_eq!(report(&request)["compatible"], true);
            request["kind"] = json!("checkpoint");
            assert_eq!(report(&request)["compatible"], false);
        }
    }

    #[test]
    fn malformed_and_bounded_descriptors_are_invalid() {
        for input in [
            b"{}".as_slice(),
            b"[]",
            b"not json",
            br#"{"kind":"checkpoint","tensors":{}}"#,
            br#"{"kind":"other","tensors":{"x":{"dtype":"F16","shape":[1]}}}"#,
            br#"{"kind":"unet","tensors":{"x":{"dtype":"F16","shape":[-1]}}}"#,
            br#"{"kind":"unet","tensors":{"x":{"dtype":"F16","shape":[true]}}}"#,
            br#"{"kind":"unet","tensors":{"x":{"dtype":"F16","shape":[2147483649]}}}"#,
            br#"{"kind":"unet","tensors":{"x":{"dtype":"F16","shape":[1,1,1,1,1,1,1,1,1]}}}"#,
        ] {
            assert_eq!(inspect(input).unwrap_err(), super::super::INVALID);
        }
        let too_many = (0..32001)
            .map(|i| (i.to_string(), json!({"dtype":"F16","shape":[1]})))
            .collect::<serde_json::Map<_, _>>();
        assert_eq!(
            inspect(&serde_json::to_vec(&json!({"kind":"unet","tensors":too_many})).unwrap())
                .unwrap_err(),
            super::super::INVALID
        );
    }

    #[test]
    fn abi_size_query_does_not_read_input_and_errors_leave_output_untouched() {
        let mut required = 0;
        let mut output = [0xab; OUTPUT_CAPACITY];
        // SAFETY: invalid input address is never read by size queries; actual error inputs are null.
        unsafe {
            assert_eq!(
                super::super::mw_check_model_compatibility(
                    std::ptr::dangling(),
                    usize::MAX,
                    std::ptr::null_mut(),
                    0,
                    &mut required
                ),
                super::super::BUFFER_SMALL
            );
            assert_eq!(required, OUTPUT_CAPACITY);
            assert_eq!(
                super::super::mw_check_model_compatibility(
                    std::ptr::dangling(),
                    usize::MAX,
                    output.as_mut_ptr(),
                    OUTPUT_CAPACITY - 1,
                    &mut required
                ),
                super::super::BUFFER_SMALL
            );
            assert_eq!(
                super::super::mw_check_model_compatibility(
                    std::ptr::null(),
                    0,
                    output.as_mut_ptr(),
                    output.len(),
                    &mut required
                ),
                super::super::INVALID
            );
            assert_eq!(
                super::super::mw_check_model_compatibility(
                    std::ptr::dangling(),
                    MAX_INPUT + 1,
                    output.as_mut_ptr(),
                    output.len(),
                    &mut required
                ),
                super::super::INVALID
            );
            assert_eq!(
                super::super::mw_check_model_compatibility(
                    std::ptr::null(),
                    0,
                    output.as_mut_ptr(),
                    output.len(),
                    std::ptr::null_mut()
                ),
                super::super::INVALID
            );
        }
        assert_eq!(output, [0xab; OUTPUT_CAPACITY]);
    }
}
