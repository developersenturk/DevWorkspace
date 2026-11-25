## Git değişikliklerine hızlı erişim

- Projenin sağ tık menüsündeki Commit edilmemiş değişiklikler (diff) seçeneği, Git ekranını ilk değişikliğin yan yana diff'iyle açar. Stage edilmiş, çalışma klasöründe değiştirilmiş ve yeni dosyalar aynı ekrandan incelenebilir.

## Geliştirme merkezi ve güncellemeler

- Geliştirme merkezi; çalışma alanı kontrollerini, çalıştırma profillerini ve günlük geliştirme araçlarını tek girişte toplar.
- Çalışan sürümden eski veya aynı sürüme ait bekleyen güncellemelerin uygulanması engellendi.

## İşler ve bakım

- Dosya menüsündeki veya Geliştirme merkezindeki İşler ve bakım ekranı beş yeni günlük geliştirme aracını bir araya getirir.
- İş setleri; ilgili projeleri, branch veya detached commit'leri, proje notlarını ve çalıştırma profilini birlikte kaydeder. Bu işe dön ile depolar kontrol edilir, güvenli branch geçişleri yapılır ve projeler açılır. Servis başlatma isteğe bağlıdır.
- Hata çözümleri; bilinen port, SDK, paket, ortam değişkeni ve servis bağlantısı hatalarına uygun inceleme veya çözüm komutları önerir. Komutlar çalıştırılmadan önce gösterilir. Port sahipleri süreç adı ve PID ile incelenebilir.
- Değişiklik etkisi; yerel .NET ve JavaScript bağımlılıklarından doğrudan/dolaylı etkilenen projeleri ve ilgili testleri çıkarır. Test komutları sırayla çalıştırılabilir; .NET testleri Debug kullanır.
- Ortam seçenekleri; bir profilden Yerel, Docker veya Sadece frontend gibi varyantlar oluşturmayı sağlar. Seçilen yerel ortam dosyasının değerleri yalnızca ilgili sürece aktarılır; mevcut .env dosyası ve sistem ayarları korunur. Ekip yapılandırması değerleri değil dosya yollarını paylaşır.
- Kaynaklar ve temizlik; yönetilen komutların ve alt süreçlerinin RAM/CPU kullanımını gösterir. Bilinen çıktılar ve önbellekler dosya/boyut önizlemesiyle çöp kutusuna taşınabilir veya onayla kalıcı temizlenebilir. Git'te izlenen dosyalar, çalışan projeler, bağlantılar ve değişmiş/eksik taramalar korunur.
