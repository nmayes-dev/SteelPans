from pathlib import Path
import os
import shutil
import subprocess
import time

URLS = [
    "https://localhost:7103",
    "https://localhost:7102",
    "https://localhost:7101",
]

FIREFOX = r"C:\Program Files\Mozilla Firefox\firefox.exe"

Path("temp").resolve().mkdir(exist_ok=True)

profile = Path("temp/firefox-steelpans").resolve()
profile.mkdir(exist_ok=True)

startup_page = Path("temp/startup_tabs.html").resolve()
startup_page.write_text(f"""
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>Steel Pans Startup</title>
</head>
<body>
<script>
{chr(10).join(f'window.open("{url}", "_blank");' for url in URLS)}
window.close();
</script>
</body>
</html>
""", encoding="utf-8")

(profile / "user.js").write_text("""
user_pref("browser.aboutwelcome.enabled", false);
user_pref("startup.homepage_welcome_url", "");
user_pref("startup.homepage_welcome_url.additional", "");
user_pref("browser.shell.checkDefaultBrowser", false);
user_pref("dom.disable_open_during_load", false);
user_pref("browser.startup.page", 0);
user_pref("browser.sessionstore.resume_from_crash", false);
user_pref("browser.tabs.warnOnClose", false);
""", encoding="utf-8")

for name in ["sessionstore.jsonlz4", "sessionstore-backups"]:
    target = profile / name

    if target.is_file():
        target.unlink()

    if target.is_dir():
        shutil.rmtree(target)

process = subprocess.Popen([
    FIREFOX,
    "-no-remote",
    "--new-window",
    "-profile",
    str(profile),
    startup_page.as_uri(),
])

try:
    input("Press enter to close browser...")
finally:
    process.terminate()
    process.wait()