# Kurulum istatistikleri

## Mimari ve sınırlar

DevWorkspace → HTTPS `POST /v1/heartbeat` → servis çekirdeği → SQL deposu.
GitHub Actions → yetkili `GET /v1/summary` → özel istatistik reposunda günlük rapor.

Uygulamada Cloudflare SDK'sı veya gizli anahtar bulunmaz. `services/telemetry/core.mjs`
Web Request/Response kullanır; `sql-store.mjs` standart SQLite SQL'i kullanır.
`worker.mjs` yalnızca D1 ve Cloudflare hız sınırı adaptörüdür.
`node-server.mjs` aynı çekirdeği Node 24+ ve SQLite üzerinde çalıştırır.

Her Windows kullanıcı profilinde rastgele UUID v4 oluşturulur. IP, makine adı,
kullanıcı adı, proje içeriği/yolları toplanmaz. Ağ sağlayıcısı bağlantıda IP'yi
görür; uygulamanın veritabanında saklanmaz. Cloudflare Worker gözlemlenebilirliği
kapalıdır. İstatistikler varsayılan olarak kapalıdır; ilk açılışta açık bir
tercih sorulur, Ayarlar'dan değiştirilebilir. Göndermeyi kapatmak geçmiş kayıtları
silmez. Günlük rapor sadece toplulaştırılmış sayıları içerir.

Bir profil birden fazla EXE çalıştırsa da aynı kurulum kimliğini kullanır.
Günlük başarılı bildirim en fazla bir kez (UTC), başarısız deneme saatte en fazla
bir kez yapılır. Uzun süre açık uygulama saatte bir kontrol eder. Sunucu kendi
saatini kullanır ve aynı kimliği tek satırda günceller.

Bu sistem lisans doğrulama değildir: açık bildirim API'sine sahte kimliklerle
istek gönderilebilir. Cloudflare adaptörü IP başına 30 istek/dakika sınırlar;
çok sayıda meşru kullanıcının aynı NAT arkasında olması bildirim geciktirebilir.
Ücretsiz kaynak limitleri ve dağıtık kötüye kullanım riski devam eder.
Tekil bilgisayar/kişi veya halen kurulu uygulama sayısı kesin olarak ölçülmez.

## Sağlayıcı değiştirme

1. Aynı protokolü sunan yeni servisi hazırla. Node sürümü için `REPORT_TOKEN`
   (en az 32 karakter), `DATABASE_PATH`, `HOST`, `PORT` ortam değişkenlerini ayarla;
   `node services/telemetry/node-server.mjs` ile çalıştır. Üretimde TLS ve hız
   sınırlayan reverse proxy kullan; Node varsayılan olarak loopback'e bağlanır.
2. Cloudflare'dan SQL dışa aktar:
   `npx wrangler d1 export devworkspace-telemetry --remote --output=telemetry.sql`.
   Yeni SQLite veritabanına aktar; `installations` tablosunu ve kimliklerini koru.
3. `config/telemetry.json` dosyasındaki `endpoint` değerini yeni HTTPS
   `/v1/heartbeat` adresine çevirip GitHub varsayılan dalına yayınla.
   İzin veren mevcut istemciler yapılandırmayı günde bir yeniler; EXE değişmez.
   Geçişte eski adresi en az yedi gün çalışır tut (geçici config hatalarında önbellek).
4. İstatistik reposundaki `TELEMETRY_SUMMARY_URL` değişkenini ve gerekiyorsa
   `TELEMETRY_REPORT_TOKEN` secret'ını yeni sunucuya göre değiştir.

GitHub yapılandırma adresi erişilemezse yalnızca yedi günden yeni önbellek
kullanılır. `enabled: false` merkezi kapatma sağlar. Yeniden izin vermeyen
istemciler yapılandırma için bile ağa çıkmaz.

GitHub yapılandırma adresinden de bağımsız çalışmak için EXE yanına
`telemetry.config.json` koy (aynı JSON biçimi) veya
`DEVWORKSPACE_TELEMETRY_URL=https://.../v1/heartbeat` ayarla.
Öncelik: ortam değişkeni → yerel dosya → GitHub yapılandırması.
Yerel ayarların dağıtımı sana aittir. Kimlik doğrulama anahtarı istemciye konmaz.

## Protokol v1

`POST /v1/heartbeat`, `Content-Type: application/json`:

```json
{"protocol":1,"installationId":"12345678-1234-4123-8123-123456789abc","version":"1.0.0.0"}
```

Başarı 204. Gövde en fazla 1024 bayt; bilinmeyen alanlar reddedilir.
`GET /v1/summary` için `Authorization: Bearer <REPORT_TOKEN>` gerekir.
`GET /health` anahtar gerektirmez; yalnızca servis/protokol sağlığını bildirir.
Rapor anahtarı sadece toplu sayıları okumaya yarar ve EXE'ye verilmez.

## Cloudflare kurulum

`services/telemetry` içinde `npm ci`, ardından `npx wrangler login`.
Sadece Workers ve D1 için gereken izinleri ver; ücretli plan seçme.
`npx wrangler d1 create devworkspace-telemetry` çıktısındaki ID'yi
`wrangler.jsonc` içine yaz. `npm run migrate` ve
`npx wrangler secret put REPORT_TOKEN` sonrasında `npm run deploy` çalıştır.
Ücretsiz planı ve kullanılan kaynakları Cloudflare panelinden kontrol et.

## GitHub raporu

`github-report.yml` ve `github-report.mjs` özel istatistik reposuna konur.
Workflow `.github/workflows/installation-statistics.yml` altında olmalı.
Repo variable `TELEMETRY_SUMMARY_URL=https://.../v1/summary` ve repo secret
`TELEMETRY_REPORT_TOKEN` ayarlanır. Workflow her gün 06:00 UTC'de (09:00 Türkiye)
veya elle çalışır. GitHub tarafında ayrı PAT gerekmez; repo kapsamlı geçici
`GITHUB_TOKEN` raporu yazar. Özel repodaki Actions kullanım kotası da geçerlidir.

## Doğrulama

`services/telemetry` içinde `npm test` (gerçek SQLite ile servis testleri).
`dotnet run --project tests/Telemetry/Telemetry.csproj -c Debug` (sahte HTTP ile
izin, günlük sınır, hata/tekrar deneme, sağlayıcı geçişi, merkezi kapatma).
