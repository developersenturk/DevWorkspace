using System.Globalization;
using System.Windows;

namespace DevWorkspace;

// Kept with the updater so it can also ship independently of the settings UI.
internal static class AppUpdateText
{
    private static readonly Dictionary<string, (string Tr, string En)> Messages = new()
    {
        ["UpdateCheck"] = ("Güncellemeleri denetle…", "Check for updates…"),
        ["UpdateWhatsNew"] = ("Yenilikler", "What's new"),
        ["UpdateNotesNew"] = ("Yeni", "New"),
        ["UpdateNotesInstalled"] = ("Kullandığınız sürüm: {0}", "Your version: {0}"),
        ["UpdateNotesCurrent"] = ("Mevcut sürüm", "Current version"),
        ["UpdateNotesHistory"] = ("Sürüm geçmişi", "Release history"),
        ["UpdateNotesLoading"] = ("Son yayınlar yükleniyor…", "Loading recent releases…"),
        ["UpdateNotesOffline"] = ("Son yayınlara ulaşılamadı. Bu sürümün ve kaydedilmiş yayınların notları gösteriliyor.", "Could not reach recent releases. Showing notes for this version and saved releases."),
        ["UpdateNotesLoaded"] = ("Son kararlı yayınlar GitHub'dan alındı.", "Recent stable releases loaded from GitHub."),
        ["UpdateNotesLocal"] = ("Bu sürümün ve kaydedilmiş yayınların notları.", "Notes for this version and saved releases."),
        ["UpdateNotesEmpty"] = ("Bu yayın için sürüm notu eklenmemiş.", "No release notes were provided for this release."),
        ["UpdateNotesRefresh"] = ("Yenile", "Refresh"),
        ["UpdateNotesGitHub"] = ("Yayını GitHub'da aç", "Open release on GitHub"),
        ["UpdateNotesClose"] = ("Kapat", "Close"),
        ["UpdateRestart"] = ("Şimdi yeniden başlat", "Restart now"),
        ["UpdateRetry"] = ("Yeniden dene", "Retry"),
        ["UpdateReleases"] = ("Kurulum paketini aç", "Open installer downloads"),
        ["UpdateDismiss"] = ("Güncelleme bildirimini gizle", "Dismiss update notification"),
        ["UpdateProgress"] = ("Uygulama güncellemesi indiriliyor", "Downloading application update"),
        ["UpdateChecking"] = ("Yeni sürüm denetleniyor…", "Checking for updates…"),
        ["UpdateAvailable"] = ("Yeni sürüm var · DevWorkspace {0}", "New version available · DevWorkspace {0}"),
        ["UpdateAvailableHint"] = ("Kullandığınız sürüm: {0}. Yeni sürümü indirmek için kurulum paketini açın.", "Your version: {0}. Open installer downloads to get the new version."),
        ["UpdateDownloading"] = ("DevWorkspace {0} indiriliyor · %{1}. Çalışmaya devam edebilirsiniz.", "Downloading DevWorkspace {0} · {1}%. You can keep working."),
        ["UpdateReady"] = ("DevWorkspace {0} hazır. Güncelleme sonraki açılışta uygulanacak.", "DevWorkspace {0} is ready. The update will be applied on the next launch."),
        ["UpdateFailed"] = ("Güncelleme indirilemedi veya doğrulanamadı. Daha sonra otomatik olarak yeniden denenecek.", "The update could not be downloaded or verified. It will be retried automatically later."),
        ["UpdateUnavailable"] = ("Otomatik güncelleme için GitHub Releases üzerindeki DevWorkspace kurulum paketini kullanın. Tek başına EXE güncellenmez.", "Install DevWorkspace using the installer from GitHub Releases to enable automatic updates. The standalone EXE does not update itself."),
        ["UpdateCurrent"] = ("DevWorkspace güncel.", "DevWorkspace is up to date."),
        ["UpdateNotificationFailed"] = ("Bildirim gösterilemedi. Güncelleme durumunu Yardım menüsünden denetleyebilirsiniz.", "Could not display the notification. Check the update status from the Help menu."),
        ["UpdateBusy"] = ("Yeniden başlatmadan önce devam eden işlemlerin tamamlanmasını bekleyin.", "Wait for running operations to finish before restarting."),
        ["UpdateOtherInstance"] = ("Başka bir DevWorkspace penceresi açık. Güncellemeyi uygulamadan önce diğer pencereleri kapatın.", "Another DevWorkspace window is open. Close the other windows before applying the update."),
        ["UpdateRestartFailed"] = ("Güncelleme uygulanamadı. DevWorkspace'i yeniden açabilirsiniz; mevcut sürümünüz korunur.", "Could not apply the update. You can reopen DevWorkspace; your existing version is preserved."),
    };

    internal static string Text(string key) => Messages.TryGetValue(key, out var value)
        ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? value.En : value.Tr) : key;

    internal static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);

    internal static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();
        foreach (var key in Messages.Keys) resources[key] = Text(key);
        return resources;
    }
}
