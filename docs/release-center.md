# Yayın Merkezi / Release Center

Proje listesinde ilgili projeye **sağ tık → Yayın Merkezi** ile açılır. Arayüz Türkçe ve İngilizcedir. Pencere seçilen projeye bağlıdır; üst menülerde bağımsız giriş bulunmaz.

## İlk kullanım

1. Yayınlayacağınız proje satırına sağ tıklayıp **Yayın Merkezi** seçin. `.csproj` doğrudan kullanılır. `.sln`/`.slnx` içindeki tek uygulama otomatik belirlenir; birden fazla uygulama varsa aynı menünün altında çözümün uygulamaları listelenir. Aynı depodaki ilgisiz bir proje seçilmez. Depo GitHub üzerinde olmalıdır.
2. Proje, depo ve uygulama kimliği arka planda doldurulur; `origin` her proje değişiminde yeniden okunur. Bu bilgiler **Kaynak ve profil** altında salt okunur olarak incelenebilir. Aynı projenin kayıtlı profili varsa kimlik, notlar ve tercihler geri yüklenir; başka projenin son kullanılan profili devralınmaz. Sürüm, kaynak sürümünden eski kalacak şekilde geri yüklenmez. **Sonraki yama**, GitHub'daki kararlı sürümleri ve formdaki sürümü dikkate alır. **Profili kaydet** seçilen projeye ait tercihleri saklar.
3. **GitHub bağlantısı** mevcut hesap/kurulum penceresini açar. Git, .NET SDK ve GitHub CLI gerekir; eksik araçlar **Yardım → Geliştirme araçları** üzerinden kurulabilir. Kimlik doğrulama GitHub CLI tarafından yönetilir; Yayın Merkezi token istemez veya saklamaz.
4. **Ön kontrol** araçları, oturum açmış hesabı, deponun origin eşleşmesini ve kaynak durumunu gösterir.

## Paket hazırlama ve yayınlama

- **Kaynak ve profil** bölümünde doğrulama projeleri düzenlenebilir. Her satır depo köküne göre bir `.csproj` yoludur. Konsol projelerinde `dotnet run`, diğerlerinde `dotnet test` çalışır. Çalıştırılacak projeleri inceleyin; boş liste doğrulama projesi çalıştırmaz.
- Commit edilmemiş değişiklikler paketlemeyi durdurur. İsterseniz aynı bölümde mesaj girip **Tüm değişiklikleri commit et** düğmesini kullanın. İşlem, yeni dosyaları da içeren değişiklik listesini onayınıza sunar; `gitignore` kuralları geçerlidir. Bu işlem başarılı olsa bile ayrıca **Dalı GitHub'a gönder** gerekir. Force push veya reset kullanılmaz.
- **Commit'lerden not oluştur**, önceki kararlı yayın etiketinden yerel HEAD'e kadar en fazla 40 commit'i düzenlenebilir metne aktarır. Etiket yerelde yoksa hata gösterilir; notları elle yazabilir veya mevcut Git araçlarıyla etiketi alabilirsiniz.
- **Paket hazırla** temiz HEAD'i `git archive` ile ayrı bir klasöre alır. Build, seçilen doğrulamalar ve publish bu kaynak kopyasında **Debug**, `win-x64`, self-contained, sıkıştırılmış tek dosya ve yerel DLL'ler dahil çalışır. Kaynak `.csproj` sürümü değiştirilmez; paket sürümü MSBuild özelliğiyle verilir. Sürüm `1.2.3` biçiminde kararlı sürüm olmalıdır.
- İlk paketlemede sabitlenmiş Velopack CLI **1.2.161**, uygulamanın kendi araç klasörüne `dotnet tool install` ile kurulur. Paketler: Setup EXE, portable ZIP, full nupkg ve iki güncelleme dizin dosyasıdır. Hedef uygulamada otomatik güncelleme için Velopack entegrasyonu bulunmalıdır. İmzalama sertifikası yönetimi bu sürüme dahil değildir.
- Tamamlanan paketin kaynak commit'i, boyutları ve SHA-256 değerleri `package.json` dosyasında kaydedilir. Yarım kalan yerel derlemeler tamamlanmış geçmişe girmez.
- **Taslağa yükle / devam et**, kaynak commit'inin GitHub'da bulunduğunu doğrular ve tam o commit'e ait taslağı oluşturur. Dosyalar sırayla yüklenir; her biri GitHub'ın boyut/durum/SHA-256 alanlarıyla doğrulanır. Aynı dosya zaten doğrulanmışsa atlanır. Ağ hatasından sonra aynı düğmeye tekrar basılabilir. GitHub'ın başarısız yüklemede bıraktığı sıfır baytlık `starter` kaydı yalnızca bu taslakta kaldırılıp yeniden yüklenir. Diğer uyuşmazlıklarda üzerine yazılmaz; yeni sürüm hazırlayın.
- **Herkese yayımla**, depo/sürüm/commit bilgisini onaya sunar. Yerel ve uzak dosyalar yeniden doğrulanır. Varsayılan olarak commit'e ait GitHub Actions çalışmalarının tamamının `completed/success` olması gerekir. Çalışması olmayan depolarda bu koşul açıkça kapatılabilir. Bu denetim GitHub'ın Actions runs listesidir; harici servis kontrollerinin veya branch protection kurallarının tam karşılığı değildir. Daha eski veya eşit bir sürüm `latest` yapılamaz. Mevcut yayımlanmış sürümler düzenlenmez.

## İstatistikler ve kontrol

**Yayınlar ve istatistikler → GitHub'dan yenile**, oturumun erişebildiği tüm yayın sayfalarını okur. Üst toplam, yayımlanmış Setup, portable ZIP ve nupkg dosyalarının birikimli `download_count` toplamıdır. Taslaklar ve dizin dosyaları bu toplama katılmaz. Ön sürümler yayımlanmışsa dahil edilir. Alttaki dosya tablosunda dizinler dahil dosyalar ayrı görülebilir.

İndirmeler **kurulum, aktif kullanım veya tekil kullanıcı** sayısı değildir; tekrar indirmeler ve test indirmeleri de GitHub sayacına yansıyabilir. Harici takip altyapısı veya istemci telemetrisi kullanılmaz. Yıldız, fork ve PR'ları da içeren açık kayıt sayısı ayrıca gösterilir. Yenileme isteğe bağlıdır; çevrimdışı hatada önceki deponun verisi güncel gibi gösterilmez. CSV dışa aktarımı görünen son anlık görüntünün tüm yayın/dosya satırlarını içerir; durum sütunu taslakları ayırt eder. Formül olarak çalışabilecek hücreler kaçırılır.

**Güncelleme indirmesini dene**, herkese açık GitHub güncellemesini ayrı bir test klasörüne gerçek Velopack SDK ile indirir ve doğrular. Kurulu uygulamaya uygulamaz, kapatmaz veya yeniden başlatmaz. Gizli depolara ait otomatik güncelleme erişimini sınamak için kullanılmaz. Sonraki açılışta uygulama ve yeniden başlatma davranışı, Setup ile kurulmuş eski uygulama üzerinden ayrıca denenmelidir.

## Geçmiş ve hatadan dönme

**Yerel paket geçmişi**, seçilen proje dosyası ve GitHub deposu için tamamlanan paketleri gösterir. **Seçili paketi kullan**, yüklemeye kaldığınız yerden devam etmek için paketin kimlik/sürüm/notlarını forma geri yükler. Başka projeye sağ tıklayıp Yayın Merkezi seçildiğinde açık pencere o projeye geçer. Ayrılırken notlar ve tercihler proje profiline otomatik kaydedilir; döndüğünüzde kendi profili geri yüklenir. Eski paket ve istatistikler yeni projeye taşınmaz. Devam eden işlem varsa proje değişimi engellenir ve önce işlemi tamamlamanız veya durdurmanız istenir. Yerel dosyalar, uzak taslak ve GitHub kontrolleri ayrı ayrı denetlenebilir.

**İşlemi durdur**, çalışan alt süreç ağacını iptal eder. Uzak sunucuya ulaşmış bir dosya kalabilir; sonraki deneme uzak durumu yeniden okur. İşlem sürerken pencerenin kapatılması ve uygulama güncellemesi için yeniden başlatılması engellenir. Aynı bilgisayardaki iki uygulama kopyası aynı Yayın Merkezi işlemini eşzamanlı yürütemez. İşlem çıktısı hem bu pencerede hem mevcut İşlem Merkezi'nde gösterilir; uzun çıktı bellekte sınırlandırılır. Disk kayıtları tam tutulur. Otomatik dosya temizliği yapılmaz.

Yerel veriler: `%LOCALAPPDATA%\DevWorkspace\ReleaseCenter\`. `profiles` proje profillerini, `packages` kaynak kopyalarını/çıktıları/manifestleri, `logs` işlem kayıtlarını, `download-checks` izole güncelleme indirmelerini ve `tools` sabitlenmiş paketleme aracını içerir. Bunlar uygulama EXE'sinin yanında dosya veya betik bulunmasını gerektirmez.

API/araç kaynakları: [GitHub CLI release create](https://cli.github.com/manual/gh_release_create), [GitHub release assets](https://docs.github.com/en/rest/releases/assets), [Velopack packaging](https://docs.velopack.io/packaging/overview).
