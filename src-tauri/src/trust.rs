//! Trusted-publisher rules.
//!
//! The launcher only ever talks to GitHub, and only to repositories owned by
//! the accounts listed in `TRUSTED_OWNERS`. That list is compiled in, so a
//! tampered catalog cannot point the launcher at someone else's files.

use reqwest::Url;

/// GitHub accounts allowed to publish the catalog and application packages.
/// Add the project's organisation here when one is created.
pub const TRUSTED_OWNERS: &[&str] = &["GavinVu"];

/// Where the application catalog lives.
pub const CATALOG_URL: &str =
    "https://raw.githubusercontent.com/GavinVu/ArchaeaSuite/main/catalog/catalog.json";

/// Debug builds can be pointed at a local catalog for testing by setting
/// `ARCHAEA_DEV_CATALOG=http://127.0.0.1:PORT/catalog.json`. Release builds
/// ignore the variable entirely.
pub fn dev_catalog_url() -> Option<String> {
    if cfg!(debug_assertions) {
        std::env::var("ARCHAEA_DEV_CATALOG").ok().filter(|v| !v.is_empty())
    } else {
        None
    }
}

pub fn catalog_url() -> String {
    dev_catalog_url().unwrap_or_else(|| CATALOG_URL.to_string())
}

fn is_dev_loopback(url: &Url) -> bool {
    dev_catalog_url().is_some() && matches!(url.host_str(), Some("127.0.0.1") | Some("localhost"))
}

fn is_trusted_owner(owner: &str) -> bool {
    TRUSTED_OWNERS.iter().any(|o| o.eq_ignore_ascii_case(owner))
}

fn segments(url: &Url) -> Vec<&str> {
    url.path_segments()
        .map(|s| s.filter(|p| !p.is_empty()).collect())
        .unwrap_or_default()
}

fn is_plain_https(url: &Url) -> bool {
    url.scheme() == "https"
        && url.username().is_empty()
        && url.password().is_none()
        && url.port().is_none()
}

/// Application packages must be GitHub Release assets of a trusted owner:
/// `https://github.com/<owner>/<repo>/releases/download/<tag>/<file>`.
pub fn is_trusted_package_url(url: &Url) -> bool {
    if is_dev_loopback(url) {
        return true;
    }
    if !is_plain_https(url) || url.host_str() != Some("github.com") {
        return false;
    }
    let s = segments(url);
    s.len() == 6 && is_trusted_owner(s[0]) && s[2] == "releases" && s[3] == "download"
}

/// Icons may be release assets or files in a trusted owner's repository:
/// `https://raw.githubusercontent.com/<owner>/<repo>/<ref>/<path>`.
pub fn is_trusted_asset_url(url: &Url) -> bool {
    if is_trusted_package_url(url) {
        return true;
    }
    if !is_plain_https(url) || url.host_str() != Some("raw.githubusercontent.com") {
        return false;
    }
    let s = segments(url);
    s.len() >= 4 && is_trusted_owner(s[0])
}

/// GitHub serves release assets through redirects to its own storage hosts.
/// Redirects anywhere else are refused.
pub fn is_trusted_redirect(url: &Url) -> bool {
    if is_dev_loopback(url) {
        return true;
    }
    if !is_plain_https(url) {
        return false;
    }
    match url.host_str() {
        Some(host) => host == "github.com" || host.ends_with(".githubusercontent.com"),
        None => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn u(s: &str) -> Url {
        Url::parse(s).unwrap()
    }

    #[test]
    fn accepts_release_assets_from_trusted_owner() {
        assert!(is_trusted_package_url(&u(
            "https://github.com/GavinVu/ArchaeaSuite/releases/download/hello-v1.0.0/hello-x64.zip"
        )));
        assert!(is_trusted_package_url(&u(
            "https://github.com/gavinvu/ArchaeaPhoto/releases/download/v1.0.0/photo.zip"
        )));
    }

    #[test]
    fn rejects_everything_else() {
        for bad in [
            "http://github.com/GavinVu/ArchaeaSuite/releases/download/v1/a.zip",
            "https://github.com/Mallory/ArchaeaSuite/releases/download/v1/a.zip",
            "https://github.com.evil.example/GavinVu/x/releases/download/v1/a.zip",
            "https://evil.example/GavinVu/x/releases/download/v1/a.zip",
            "https://github.com/GavinVu/x/archive/refs/heads/main.zip",
            "https://github.com/GavinVu/x/releases/download/v1/../../../Mallory/a.zip",
            "https://GavinVu@github.com/GavinVu/x/releases/download/v1/a.zip",
            "https://github.com:8443/GavinVu/x/releases/download/v1/a.zip",
            "https://raw.githubusercontent.com/GavinVu/x/main/a.zip",
        ] {
            assert!(!is_trusted_package_url(&u(bad)), "should reject {bad}");
        }
    }

    #[test]
    fn asset_urls() {
        assert!(is_trusted_asset_url(&u(
            "https://raw.githubusercontent.com/GavinVu/ArchaeaSuite/main/catalog/icons/a.png"
        )));
        assert!(!is_trusted_asset_url(&u(
            "https://raw.githubusercontent.com/Mallory/ArchaeaSuite/main/a.png"
        )));
        assert!(!is_trusted_asset_url(&u("https://example.com/a.png")));
    }

    #[test]
    fn redirects_stay_on_github() {
        assert!(is_trusted_redirect(&u(
            "https://release-assets.githubusercontent.com/github-production-release-asset/1/2"
        )));
        assert!(is_trusted_redirect(&u("https://objects.githubusercontent.com/x")));
        assert!(!is_trusted_redirect(&u("https://githubusercontent.com.evil.example/x")));
        assert!(!is_trusted_redirect(&u("http://objects.githubusercontent.com/x")));
    }
}
