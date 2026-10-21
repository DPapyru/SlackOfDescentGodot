use encoding_rs::SHIFT_JIS;
use gml_parser::ast::{Expr, AST};
use gm8exe::asset::{CodeAction, PascalString};
use serde_json::{json, Value};
use std::{collections::BTreeMap, env, fs, path::Path};

fn text(bytes: &[u8]) -> String {
    if let Ok(value) = std::str::from_utf8(bytes) {
        value.to_owned()
    } else {
        SHIFT_JIS.decode(bytes).0.into_owned()
    }
}

fn string(value: &PascalString) -> String { text(&value.0) }

fn uncomment(source: &str) -> String {
    let chars: Vec<_> = source.chars().collect();
    let mut result = String::new();
    let mut i = 0;
    let mut quote = None;
    while i < chars.len() {
        let c = chars[i];
        if let Some(q) = quote {
            result.push(c);
            if c == q { quote = None; }
            i += 1;
        } else if c == '\'' || c == '"' {
            quote = Some(c); result.push(c); i += 1;
        } else if c == '/' && chars.get(i+1) == Some(&'/') {
            i += 2;
            while i < chars.len() && chars[i] != '\n' { i += 1; }
        } else if c == '/' && chars.get(i+1) == Some(&'*') {
            i += 2;
            while i+1 < chars.len() && !(chars[i] == '*' && chars[i+1] == '/') { i += 1; }
            i = (i+2).min(chars.len()); result.push(' ');
        } else { result.push(c); i += 1; }
    }
    result
}

fn expr(e: &Expr, calls: &mut BTreeMap<String, usize>) -> Value {
    match e {
        Expr::LiteralIdentifier(v) => json!(["id", text(v)]),
        Expr::LiteralReal(v) => json!(["num", v]),
        Expr::LiteralString(v) => json!(["str", text(v)]),
        Expr::Unary(v) => json!(["unary", format!("{:?}", v.op), expr(&v.child, calls)]),
        Expr::Binary(v) => json!(["binary", format!("{:?}", v.op), expr(&v.left, calls), expr(&v.right, calls)]),
        Expr::Group(v) => json!(["group", v.iter().map(|v| expr(v, calls)).collect::<Vec<_>>()]),
        Expr::Function(v) => {
            let name = text(v.name);
            *calls.entry(name.clone()).or_default() += 1;
            json!(["call", name, v.params.iter().map(|v| expr(v, calls)).collect::<Vec<_>>()])
        },
        Expr::If(v) => json!(["if", expr(&v.cond, calls), expr(&v.body, calls), v.else_body.as_ref().map(|v| expr(v, calls))]),
        Expr::For(v) => json!(["for", expr(&v.start, calls), expr(&v.cond, calls), expr(&v.step, calls), expr(&v.body, calls)]),
        Expr::While(v) => json!(["while", expr(&v.cond, calls), expr(&v.body, calls)]),
        Expr::DoUntil(v) => json!(["until", expr(&v.cond, calls), expr(&v.body, calls)]),
        Expr::Repeat(v) => json!(["repeat", expr(&v.count, calls), expr(&v.body, calls)]),
        Expr::With(v) => json!(["with", expr(&v.target, calls), expr(&v.body, calls)]),
        Expr::Switch(v) => json!(["switch", expr(&v.input, calls), expr(&v.body, calls)]),
        Expr::Case(v) => json!(["case", expr(v, calls)]),
        Expr::Var(v) => json!(["var", v.vars.iter().map(|v| text(v)).collect::<Vec<_>>()]),
        Expr::GlobalVar(v) => json!(["globalvar", v.vars.iter().map(|v| text(v)).collect::<Vec<_>>()]),
        Expr::Return(v) => json!(["return", expr(v, calls)]),
        Expr::Default => json!(["default"]),
        Expr::Continue => json!(["continue"]),
        Expr::Break => json!(["break"]),
        Expr::Exit => json!(["exit"]),
    }
}

struct Export {
    calls: BTreeMap<String, usize>,
    errors: Vec<Value>,
    code: BTreeMap<String, Value>,
    source: BTreeMap<String, String>,
}

impl Export {
    fn parse(&mut self, key: String, bytes: &[u8]) -> String {
        if !bytes.is_empty() { self.source.insert(key.clone(), text(bytes)); }
        match AST::new(bytes) {
            Ok(ast) => {
                let nodes: Vec<_> = ast.iter().map(|e| expr(e, &mut self.calls)).collect();
                self.code.insert(key.clone(), json!(["group", nodes]));
            },
            Err(err) => self.errors.push(json!({"key":key,"error":err.to_string()})),
        }
        key
    }

    fn actions(&mut self, key: &str, actions: &[CodeAction]) -> Vec<Value> {
        actions.iter().enumerate().filter(|(_,a)| a.id != 605).map(|(i,a)| {
            let code_key = format!("{key}/{i}");
            let variable_source;
            let source = if a.action_kind == 6 {
                let mut bytes = a.param_strings[0].0.to_vec();
                bytes.extend_from_slice(if a.is_relative { b"+=" } else { b"=" });
                bytes.extend_from_slice(&a.param_strings[1].0);
                variable_source = PascalString(bytes.into_boxed_slice());
                &variable_source
            } else if a.action_kind == 7 && a.param_count > 0 { &a.param_strings[0] } else { &a.fn_code };
            let code = if a.execution_type == 2 || a.id == 603 { Some(self.parse(code_key, &source.0)) } else { None };
            let parameters: Vec<_> = if code.is_some() { Vec::new() } else { a.param_strings.iter().take(a.param_count).enumerate().map(|(pi,p)| {
                if matches!(a.param_types[pi], 1 | 2) { json!(["str",string(p)]) }
                else { match AST::expression(&p.0) { Ok(e) => expr(&e,&mut self.calls), Err(e) => { self.errors.push(json!({"key":format!("{key}/{i}/parameter/{pi}"),"error":e.to_string()})); Value::Null } } }
            }).collect() };
            json!({"id":a.id,"kind":a.action_kind,"execution":a.execution_type,"target":a.applies_to,
                "condition":a.is_condition,"invert":a.invert_condition,"relative":a.is_relative,
                "function":string(&a.fn_name),"code":code,
                "parameters":parameters,
                "parameterTypes":a.param_types.iter().take(a.param_count).collect::<Vec<_>>()})
        }).collect()
    }
}

fn image(path: &Path, width: u32, height: u32, pixels: &[u8], bgra: bool) {
    if width == 0 || height == 0 || pixels.is_empty() { return; }
    let mut bytes = pixels.to_vec();
    if bgra { for p in bytes.chunks_exact_mut(4) { p.swap(0, 2); } }
    let file = fs::File::create(path).unwrap();
    let mut encoder = png::Encoder::new(file, width, height);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    encoder.write_header().unwrap().write_image_data(&bytes).unwrap();
}

fn main() {
    let args: Vec<_> = env::args().collect();
    if args.get(1).map(String::as_str) == Some("--parse") {
        use std::io::Read;
        let mut bytes = Vec::new();
        std::io::stdin().read_to_end(&mut bytes).unwrap();
        let mut calls = BTreeMap::new();
        match AST::new(&bytes) {
            Ok(ast) => println!("{}",json!(["group",ast.iter().map(|e| expr(e,&mut calls)).collect::<Vec<_>>() ])),
            Err(e) => { eprintln!("{e}"); std::process::exit(1); }
        }
        return;
    }
    assert!(args.len() >= 3, "slack-extract SOURCE_DIRECTORY OUTPUT_DIRECTORY [SOURCE_ARCHIVE]");
    let source = Path::new(&args[1]);
    let output = Path::new(&args[2]);
    for sub in ["sprites", "backgrounds", "sounds", "fonts"] { fs::create_dir_all(output.join(sub)).unwrap(); }
    let assets = gm8exe::reader::from_exe(fs::read(source.join("SlackOfDescent.exe")).unwrap(), None::<fn(&str)>, true, true).unwrap();
    let mut export = Export { calls: BTreeMap::new(), errors: Vec::new(), code: BTreeMap::new(), source: BTreeMap::new() };
    let scripts: Vec<_> = assets.scripts.iter().enumerate().filter_map(|(id,s)| s.as_ref().map(|s| {
        json!({"id":id,"name":string(&s.name),"code":export.parse(format!("scripts/{}",string(&s.name)), &s.source.0)})
    })).collect();
    let objects: Vec<_> = assets.objects.iter().enumerate().filter_map(|(id,o)| o.as_ref().map(|o| {
        let events: Vec<_> = o.events.iter().enumerate().flat_map(|(kind,list)| list.iter().map(move |(sub,actions)| (kind,sub,actions))).map(|(kind,sub,actions)| {
            json!({"kind":kind,"sub":sub,"actions":export.actions(&format!("objects/{}/{kind}/{sub}",string(&o.name)), actions)})
        }).collect();
        json!({"id":id,"name":string(&o.name),"sprite":o.sprite_index,"solid":o.solid,"visible":o.visible,"depth":o.depth,"persistent":o.persistent,"parent":o.parent_index,"mask":o.mask_index,"events":events})
    })).collect();
    let rooms: Vec<_> = assets.rooms.iter().enumerate().filter_map(|(id,r)| r.as_ref().map(|r| {
        let instances: Vec<_> = r.instances.iter().map(|i| json!({"id":i.id,"object":i.object,"x":i.x,"y":i.y,"xscale":i.xscale,"yscale":i.yscale,"blend":i.blend,"angle":i.angle,"code":export.parse(format!("rooms/{id}/instances/{}",i.id),&i.creation_code.0)})).collect();
        let tiles: Vec<_> = r.tiles.iter().map(|t| json!({"id":t.id,"background":t.source_bg,"x":t.x,"y":t.y,"sourceX":t.tile_x,"sourceY":t.tile_y,"width":t.width,"height":t.height,"depth":t.depth,"xscale":t.xscale,"yscale":t.yscale,"blend":t.blend})).collect();
        let views: Vec<_> = r.views.iter().map(|v| json!({"visible":v.visible,"x":v.source_x,"y":v.source_y,"width":v.source_w,"height":v.source_h,"portX":v.port_x,"portY":v.port_y,"portWidth":v.port_w,"portHeight":v.port_h,"target":v.following.target,"hborder":v.following.hborder,"vborder":v.following.vborder,"hspeed":v.following.hspeed,"vspeed":v.following.vspeed})).collect();
        let backgrounds: Vec<_> = r.backgrounds.iter().map(|b| json!({"visible":b.visible_on_start,"foreground":b.is_foreground,"background":b.source_bg,"x":b.xoffset,"y":b.yoffset,"tileX":b.tile_horz,"tileY":b.tile_vert,"hspeed":b.hspeed,"vspeed":b.vspeed,"stretch":b.stretch})).collect();
        json!({"id":id,"name":string(&r.name),"caption":string(&r.caption),"width":r.width,"height":r.height,"speed":r.speed,"persistent":r.persistent,"colour":u32::from(r.bg_colour),"clearScreen":r.clear_screen,"clearRegion":r.clear_region,"viewsEnabled":r.views_enabled,"views":views,"backgrounds":backgrounds,"code":export.parse(format!("rooms/{id}/create"),&r.creation_code.0),"instances":instances,"tiles":tiles})
    })).collect();
    let sprites: Vec<_> = assets.sprites.iter().enumerate().filter_map(|(id,s)| s.as_ref().map(|s| {
        let frames: Vec<_> = s.frames.iter().enumerate().map(|(f,p)| {
            let path = format!("sprites/{id}_{f}.png");
            image(&output.join(&path),p.width,p.height,&p.data,true);
            json!({"path":path,"width":p.width,"height":p.height})
        }).collect();
        let masks: Vec<_> = s.colliders.iter().map(|c| json!({"width":c.width,"height":c.height,"left":c.bbox_left,"right":c.bbox_right,"top":c.bbox_top,"bottom":c.bbox_bottom,"data":c.data.iter().map(|v| if *v {'1'} else {'0'}).collect::<String>()})).collect();
        json!({"id":id,"name":string(&s.name),"originX":s.origin_x,"originY":s.origin_y,"frames":frames,"perFrameMasks":s.per_frame_colliders,"masks":masks})
    })).collect();
    let backgrounds: Vec<_> = assets.backgrounds.iter().enumerate().filter_map(|(id,b)| b.as_ref().map(|b| {
        let path = format!("backgrounds/{id}.png");
        if let Some(data) = &b.data { image(&output.join(&path),b.width,b.height,data,true); }
        json!({"id":id,"name":string(&b.name),"width":b.width,"height":b.height,"path":path})
    })).collect();
    let sounds: Vec<_> = assets.sounds.iter().enumerate().filter_map(|(id,s)| s.as_ref().map(|s| {
        let path = format!("sounds/{id}{}",string(&s.extension));
        if let Some(data) = &s.data { fs::write(output.join(&path),data).unwrap(); }
        json!({"id":id,"name":string(&s.name),"path":path,"volume":s.volume,"pan":s.pan,"kind":s.kind as u32,"present":s.data.is_some(),"effects":{"chorus":s.fx.chorus,"echo":s.fx.echo,"flanger":s.fx.flanger,"gargle":s.fx.gargle,"reverb":s.fx.reverb}})
    })).collect();
    let fonts: Vec<_> = assets.fonts.iter().enumerate().filter_map(|(id,f)| f.as_ref().map(|f| {
        let path = format!("fonts/{id}.png");
        let rgba: Vec<_> = f.pixel_map.iter().flat_map(|a| [255,255,255,*a]).collect();
        image(&output.join(&path),f.map_width,f.map_height,&rgba,false);
        json!({"id":id,"name":string(&f.name),"systemName":string(&f.sys_name),"size":f.size,"bold":f.bold,"italic":f.italic,"rangeStart":f.range_start,"rangeEnd":f.range_end,"charset":f.charset,"aa":f.aa_level,"path":path,"map":f.dmap.to_vec()})
    })).collect();
    let constants: Vec<_> = assets.constants.iter().map(|c| json!({"name":string(&c.name),"value":string(&c.expression)})).collect();
    let library: Vec<_> = assets.library_init_strings.iter().enumerate().map(|(i,c)| export.parse(format!("library/{i}"),&c.0)).collect();
    let mut external = Vec::new();
    let mut media = BTreeMap::new();
    fn walk(dir: &Path, base: &Path, output: &Path, export: &mut Export, files: &mut Vec<Value>, media: &mut BTreeMap<String, Value>) {
        let mut entries: Vec<_> = fs::read_dir(dir).unwrap().map(|v| v.unwrap().path()).collect();
        entries.sort();
        for path in entries {
            if path.is_dir() {
                if path.file_name().unwrap() != "__history" { walk(&path,base,output,export,files,media); }
            } else {
                let relative = path.strip_prefix(base).unwrap().to_string_lossy().replace('\\',"/");
                if path.extension().and_then(|v| v.to_str()) == Some("txt") && !relative.contains("README") {
                    let bytes = fs::read(&path).unwrap();
                    let key = export.parse(format!("external/{relative}"),&bytes);
                    files.push(json!({"path":relative,"code":key}));
                } else if path.extension().and_then(|v| v.to_str()).map(|v|v.eq_ignore_ascii_case("gif")).unwrap_or(false) {
                    let mut options = gif::DecodeOptions::new();
                    options.set_color_output(gif::ColorOutput::RGBA);
                    let mut reader = options.read_info(fs::File::open(&path).unwrap()).unwrap();
                    let width = reader.width() as u32;
                    let height = reader.height() as u32;
                    let mut canvas = vec![0u8; (width*height*4) as usize];
                    let mut frames = Vec::new();
                    while let Some(frame) = reader.read_next_frame().unwrap() {
                        let previous = canvas.clone();
                        for y in 0..frame.height as usize { for x in 0..frame.width as usize {
                            let src = (y*frame.width as usize+x)*4;
                            let dst = ((y+frame.top as usize)*width as usize+x+frame.left as usize)*4;
                            if frame.buffer[src+3] != 0 { canvas[dst..dst+4].copy_from_slice(&frame.buffer[src..src+4]); }
                        }}
                        let name = format!("external/{relative}/{}.png",frames.len());
                        fs::create_dir_all(output.join(&name).parent().unwrap()).unwrap();
                        image(&output.join(&name),width,height,&canvas,false);
                        frames.push(json!({"path":name,"width":width,"height":height}));
                        match frame.dispose {
                            gif::DisposalMethod::Background => { for y in 0..frame.height as usize { for x in 0..frame.width as usize {
                                let dst = ((y+frame.top as usize)*width as usize+x+frame.left as usize)*4;
                                canvas[dst..dst+4].fill(0);
                            }}},
                            gif::DisposalMethod::Previous => canvas = previous,
                            _ => (),
                        }
                    }
                    media.insert(relative,json!({"frames":frames,"originX":0,"originY":0,"masks":[]}));
                } else if path.extension().and_then(|v|v.to_str()).map(|s|s.eq_ignore_ascii_case("png")).unwrap_or(false) {
                    let name = format!("assets/{relative}");
                    fs::create_dir_all(output.join(&name).parent().unwrap()).unwrap();
                    fs::copy(&path,output.join(&name)).unwrap();
                    let reader = png::Decoder::new(fs::File::open(&path).unwrap()).read_info().unwrap();
                    let info = reader.info();
                    media.insert(relative,json!({"frames":[{"path":name,"width":info.width,"height":info.height}],"originX":0,"originY":0,"masks":[]}));
                } else if path.extension().and_then(|v|v.to_str()).map(|s|s.eq_ignore_ascii_case("bmp")).unwrap_or(false) {
                    let bytes = fs::read(&path).unwrap();
                    let name = format!("assets/{relative}");
                    fs::create_dir_all(output.join(&name).parent().unwrap()).unwrap();
                    fs::copy(&path,output.join(&name)).unwrap();
                    let width = i32::from_le_bytes(bytes[18..22].try_into().unwrap()).unsigned_abs();
                    let height = i32::from_le_bytes(bytes[22..26].try_into().unwrap()).unsigned_abs();
                    media.insert(relative,json!({"frames":[{"path":name,"width":width,"height":height}],"originX":0,"originY":0,"masks":[]}));
                } else if matches!(path.extension().and_then(|v|v.to_str()).map(|s|s.to_ascii_lowercase()).as_deref(),Some("wav" | "ogg" | "mp3" | "mid")) {
                    let dest = output.join("assets").join(&relative);
                    fs::create_dir_all(dest.parent().unwrap()).unwrap();
                    fs::copy(&path,dest).unwrap();
                }
            }
        }
    }
    walk(&source.join("Chara"),source,output,&mut export,&mut external,&mut media);
    walk(&source.join("Sound"),source,output,&mut export,&mut external,&mut media);
    let items_path = source.join("Chara/Chest/Items");
    let items_key = export.parse("external/Chara/Chest/Items".into(), &fs::read(items_path).unwrap());
    external.push(json!({"path":"Chara/Chest/Items","code":items_key}));
    for name in ["GameSetting","SurvivalSetting","StripSetting","NudeSetting.txt"] {
        let path = source.join("Cfg").join(name);
        if path.exists() {
            let key = export.parse(format!("external/Cfg/{name}"),&fs::read(path).unwrap());
            external.push(json!({"path":format!("Cfg/{name}"),"code":key}));
        }
    }
    fs::create_dir_all(output.join("assets/Cfg")).unwrap();
    for name in ["KeyConfig","itemData","data"] {
        if source.join("Cfg").join(name).exists() { fs::copy(source.join("Cfg").join(name),output.join("assets/Cfg").join(name)).unwrap(); }
    }
    let counts = json!({"scripts":scripts.len(),"objects":objects.len(),"rooms":rooms.len(),"sprites":sprites.len(),"sounds":sounds.len(),"backgrounds":backgrounds.len(),"fonts":fonts.len(),"external":external.len(),"codeUnits":export.code.len(),"parseErrors":export.errors.len()});
    let manifest = json!({"version":format!("{:?}",assets.version),"counts":counts,"scripts":scripts,"objects":objects,"rooms":rooms,"sprites":sprites,"backgrounds":backgrounds,"sounds":sounds,"fonts":fonts,"constants":constants,"roomOrder":assets.room_order,"library":library,"external":external,"lastInstanceId":assets.last_instance_id,"lastTileId":assets.last_tile_id});
    fs::write(output.join("manifest.json"),serde_json::to_vec(&manifest).unwrap()).unwrap();
    fs::write(output.join("media.json"),serde_json::to_vec(&media).unwrap()).unwrap();
    fs::write(output.join("code.json"),serde_json::to_vec(&export.code).unwrap()).unwrap();
    let sources: BTreeMap<_,_> = export.source.iter().filter(|(k,_)|k.starts_with("external/")).map(|(k,v)|(k.trim_start_matches("external/"),uncomment(v))).collect();
    fs::write(output.join("sources.json"),serde_json::to_vec(&sources).unwrap()).unwrap();
    fs::write(output.join("analysis.json"),serde_json::to_vec_pretty(&json!({"counts":counts,"calls":export.calls,"errors":export.errors})).unwrap()).unwrap();
    if let Some(path) = args.get(3) { fs::write(path,serde_json::to_vec_pretty(&export.source).unwrap()).unwrap(); }
    println!("{counts}");
}
