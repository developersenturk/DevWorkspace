# Günlük çalışma araçları

Araçlara **Görünüm → Çalışma alanı araçları** üzerinden ulaşılır. Mevcut çalışma alanındaki etkin projeler kullanılır; çalışma alanı değiştiğinde araç penceresi kapanır.

## Çalışma ortamını tek tıkla başlat

**Dosya → Başlatma profilleri** altında profil oluştur. Her adımda proje ve işlem seç:

- Projeyi IDE'de aç.
- Proje klasöründe PowerShell terminali aç.
- Hazır veya projeye kaydedilmiş bir komutu çalıştır.
- Compose dosyası olan projelerde servisleri `docker compose up` ile başlat.

Kaydettiğin profil aynı menüden doğrudan çalıştırılır. Aynı klasörde aynı komut zaten çalışıyorsa ikinci süreç açılmaz. Uzun süren komutlar arka planda birlikte çalışabilir; adımlar servislerin hazır olmasını beklemez. Komut çıktıları çalışan uygulamalar ve işlem merkezinde görünür.

## Projeye dönüş notu

Projeyi seç, sağdaki **Projeye dönüş notu** kartından **Dönüş notunu düzenle** düğmesine bas. “Nerede kaldım?” ve “Sıradaki iş” alanlarını kaydet. Not seçili projede tekrar gösterilir. İki alanı boş kaydetmek notu kaldırır.

Notlar bilgisayardaki `session.json` dosyasında çalışma alanına göre saklanır; kaynak depoya yazılmaz.

## Toplu Git özeti

**Toplu Git özeti → Yenile**, aynı depoya ait projeleri tek satırda toplar. Dal, izlenen dal, ileri/geri commit sayısı, değişiklik ve stash sayısını gösterir. İzlenen dal olmadığında ileri/geri sayıları boş kalır.

İşlem otomatik fetch/pull/push yapmaz. Güncel uzak dal bilgisi için mevcut **Git → Toplu fetch** işlemini kullan. Seçili depoyu **Git işlemlerini aç** ile inceleyebilirsin.

## Çalışan uygulamalar

DevWorkspace üzerinden başlatılan komutların PID, durum, günlük ve alt süreçlerine ait TCP dinleme adreslerini gösterir. **Durdur** süreç ağacını sonlandırır. Durduktan sonra **Yeniden başlat** kullanılabilir.

Başka uygulamalardan başlatılmış süreçler bu merkez tarafından yönetilmez. IDE ve bağımsız terminal pencereleri listeye dahil değildir. DevWorkspace kapandığında yönetilen komutlar sonlandırılır; yalnızca araç penceresini kapatmak bu komutları durdurmaz.

## Ortam kontrolü

Proje seçip **Kontrol et** düğmesine bas. Mevcut araç/SDK denetimlerine ek olarak:

- Node sürümü `.nvmrc`, `.node-version` veya `package.json` içindeki `engines.node` ile karşılaştırılır. Yaygın sayısal, karşılaştırmalı, `^`, `~`, joker ve `||` aralıkları desteklenir. `lts/*` gibi çözümlenemeyen istekler elle doğrulama olarak gösterilir.
- `.env.example`, `.env.sample` veya `.env.template` dosyasındaki anahtarlar `.env` ile karşılaştırılır. Değerler gösterilmez ve geçerlilikleri denetlenmez.
- Docker Compose için mevcut konteynerlerin çalışma durumu kontrol edilir. Henüz oluşturulmamış servislerin tamamı için kapsamlı sağlık testi yapılmaz.

## Projeler arası içerik araması

**Ctrl+Shift+F** ile kaynak dosyaların içeriğinde düz metin ara. Proje kapsamını ve harf duyarlılığını seçebilirsin. Sonuçlarda dosya, satır numarası ve eşleşme çevresindeki metin gösterilir. Çift tıkla veya **Dosyayı aç** düğmesini kullan; VS Code varsa ilgili satır açılır, aksi halde Not Defteri kullanılır.

Üretilen klasörler, Git verileri, yerel `.env`, ikili dosyalar ve 2 MB üzeri dosyalar atlanır. Sonuç sınırı 2.000, taranan metin dosyası sınırı 50.000'dir. Tarama iptal edilebilir; atlanan öğe sayısı sonuçta görünür.

## Bağımlılık ve etki haritası

**Tara**, `.NET ProjectReference`, JavaScript `file:`, `link:`, `workspace:` ve Compose `depends_on` ilişkilerini bulur. Compose için Docker CLI gerekir; yapılandırma değişken değerlerini yerine koymadan okunur ve servis ilişkileri çıkarılır. Bir dosya/proje/servis seçerek doğrudan ve dolaylı gereksinimleri ve değişiklikten etkilenebilecek projeleri gör. **Tüm bağlantılar** sekmesi kaynak/hedef ilişkilerini listeler. Döngüler sonlu olarak işlenir.

Dinamik MSBuild ifadeleri, API çağrıları ve çalışma anında kurulan servis ilişkileri otomatik çözümlenmez. Atlanan/çözümlenemeyen öğelerin ayrıntıları sonuç metninin araç ipucundadır.

## Yeni proje şablonları

Hazır .NET Console, ASP.NET Core API ve Node.js şablonlarından birini seç veya **Klasörden şablon ekle** ile kendi klasörünü kaydet. Klasör kaydı yerel oturumda tutulur; kaynak şablon değiştirilmez.

Dosya adlarında `__PROJECT_NAME__`, UTF-8 metin içinde `{{ProjectName}}` yer tutucusu kullanılır. Önce proje adı ve üst klasörü girip **Önizle** düğmesine bas. Dosya listesini gördükten sonra **Oluştur** yeni klasörü yazar. İstersen Git deposu da başlatılır. Paket kurulumu ve ilk commit otomatik yapılmaz.

Mevcut hedef klasörün üzerine yazılmaz. `.git`, derleme çıktıları, yerel `.env`, `.pem`, `.pfx`, `.p12` ve `.key` dosyaları kopyalanmaz; `.env.example` ve `.env.sample` korunur. Sembolik bağlantı veya okunamayan klasör içeren şablonlar reddedilir. Sınırlar 2.000 dosya, dosya başına 10 MB ve toplam 50 MB'dir. Yeni klasör çalışma alanı tarama kapsamındaysa otomatik yeniden taramada görünür.

## Geliştirici doğrulaması

```powershell
dotnet run --project tests/WorkspaceFeatures/WorkspaceFeatures.csproj -c Debug
```

Kontroller geçici dosya ve depolarda arama, ortam sürümleri, bağımlılık döngüleri, şablon güvenliği, oturum uyumluluğu, Git özeti ve gerçek bir yerel dinleyici sürecinin port takibi/sonlandırılmasını sınar.
