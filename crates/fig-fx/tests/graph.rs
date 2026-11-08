//! Node-graph effects, against the same cases as the C# `EffectGraphTests`.

use fig_fx::graph::{GraphEffect, GraphEffectLibrary, GraphErrorCode, compile};
use fig_fx::{Frame, effects};
use fig_timeline::{EffectGraph, GraphEdge, GraphNode, ParamMap, ParamValue, PromotedParam, PromotedTarget};

fn node(g: &mut EffectGraph, primitive: &str, id: &str, params: ParamMap, effect: Option<&str>) {
    g.nodes.push(GraphNode {
        id: id.into(),
        primitive_id: primitive.into(),
        params,
        effect_type_id: effect.map(Into::into),
        ..Default::default()
    });
}

fn wire(g: &mut EffectGraph, from: &str, fp: &str, to: &str, tp: &str) {
    g.edges.push(GraphEdge {
        from_node_id: from.into(),
        from_port: fp.into(),
        to_node_id: to.into(),
        to_port: tp.into(),
    });
}

fn through(middle: &str, params: ParamMap, effect: Option<&str>) -> EffectGraph {
    let mut g = EffectGraph { name: "Test".into(), ..Default::default() };
    node(&mut g, "input", "in", ParamMap::new(), None);
    node(&mut g, middle, "mid", params, effect);
    node(&mut g, "output", "out", ParamMap::new(), None);
    wire(&mut g, "in", "image", "mid", "image");
    wire(&mut g, "mid", "image", "out", "image");
    g
}

fn picture(seed: u32) -> Frame {
    let mut state = seed.wrapping_mul(2654435761).max(1);
    let mut px = vec![0u8; 32 * 20 * 4];
    for (i, b) in px.iter_mut().enumerate() {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        *b = if i % 4 == 3 { 255 } else { state as u8 };
    }
    Frame::from_pixels(32, 20, px)
}

#[test]
fn compile_reports_problems_on_the_right_node_and_rejects_loops() {
    let mut g = through("blur", ParamMap::new(), None);
    node(&mut g, "nonsense", "bad", ParamMap::new(), None);
    node(&mut g, "mix", "mix", ParamMap::new(), None);
    wire(&mut g, "in", "image", "mix", "a");
    wire(&mut g, "mix", "image", "out", "image"); // a second wire into the output
    let r = compile(&g);
    assert!(!r.is_valid());
    assert!(r.errors.iter().any(|e| e.code == GraphErrorCode::UnknownPrimitive && e.node_id.as_deref() == Some("bad")));
    assert!(r.errors.iter().any(|e| e.code == GraphErrorCode::InputAlreadyConnected));

    let mut looped = through("blur", ParamMap::new(), None);
    wire(&mut looped, "mid", "image", "mid", "image");
    assert!(
        compile(&looped)
            .errors
            .iter()
            .any(|e| matches!(e.code, GraphErrorCode::Cycle | GraphErrorCode::InputAlreadyConnected))
    );
}

#[test]
fn a_stock_effect_node_matches_running_the_effect_directly() {
    let params = ParamMap::from([("amount".to_string(), ParamValue::Double(0.4))]);
    let g = through("effect", params.clone(), Some("brightness"));
    assert!(compile(&g).is_valid());
    let frame = picture(1);
    let via_graph = GraphEffect::new(&g).apply(&frame, &ParamMap::new(), 0.0).unwrap();
    let direct = effects::find("brightness").unwrap().apply(&frame, &params, 0.0).unwrap();
    assert_eq!(via_graph, direct);
}

#[test]
fn an_invalid_graph_passes_the_picture_through_and_a_valid_one_is_repeatable() {
    let bad = EffectGraph::default();
    assert!(GraphEffect::new(&bad).apply(&picture(2), &ParamMap::new(), 0.0).is_none());
    let g = through("levels", ParamMap::from([("gamma".to_string(), ParamValue::Double(1.6))]), None);
    let fx = GraphEffect::new(&g);
    let frame = picture(3);
    let copy = frame.clone();
    let a = fx.apply(&frame, &ParamMap::new(), 0.5).unwrap();
    let b = fx.apply(&frame, &ParamMap::new(), 0.5).unwrap();
    assert_eq!(a, b);
    assert_eq!(frame, copy);
}

#[test]
fn a_saved_graph_becomes_an_effect_with_its_promoted_controls() {
    let mut g = through("blur", ParamMap::new(), None);
    g.promoted.push(PromotedParam {
        key: "soft".into(),
        label: "Softness".into(),
        targets: vec![PromotedTarget { node_id: "mid".into(), param: "radius".into(), scale: 1.0 }],
        ..Default::default()
    });
    let mut lib = GraphEffectLibrary::attach(Vec::new());
    assert!(lib.save(&g).is_valid());
    let entry = effects::find(&g.type_id()).unwrap();
    assert!(entry.is_custom);
    assert_eq!(entry.params[0].label, "Softness");
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("fx.json");
    lib.export(&g.id, &path).unwrap();
    let imported = lib.import(&path).unwrap().id.clone();
    assert_ne!(imported, g.id); // the id was taken
    drop(lib);
    assert!(effects::find(&g.type_id()).is_none());
}
