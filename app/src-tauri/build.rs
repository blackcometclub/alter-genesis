fn main() {
    println!("cargo:rerun-if-env-changed=ALTER_GENESIS_GOOGLE_CLIENT_PATH");
    let client = if let Some(path) = std::env::var_os("ALTER_GENESIS_GOOGLE_CLIENT_PATH") {
        println!(
            "cargo:rerun-if-changed={}",
            std::path::Path::new(&path).display()
        );
        std::fs::read(path).expect("Google client configuration could not be read")
    } else {
        b"{}".to_vec()
    };
    let output = std::path::PathBuf::from(std::env::var_os("OUT_DIR").expect("OUT_DIR"));
    std::fs::write(output.join("google-client.json"), client)
        .expect("Google client configuration could not be embedded");
    tauri_build::build()
}
