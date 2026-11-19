## Günlük çalışma araçları

- Açılış ekranı yenilendi: marka alanı, sürüm bilgisi ve gerçek hazırlık aşamaları açık/koyu temayla uyumlu bir düzende gösteriliyor. Açılışa ek bekleme süresi eklenmedi.

- İşlem Merkezi, toplu fetch, worktree yönetimi, proje inceleme, takip edilen bulgular ve çalışma alanı araçlarındaki tablolar DevExpress GridControl ile birleştirildi. Koyu/açık tema, satır seçimi ve işlem düğmeleri aynı görünüm ve davranışı kullanır.

- Pencereler menüsü açılırken kayıtlı düzenlerin yeniden oluşturulması kaldırıldı; menü açılışında odak/yakalama sorunlarına yol açabilen bu işlem artık başlangıçta ve düzen kaydedildiğinde yapılıyor.

- Başlatma profillerine terminal, kayıtlı komut ve Compose adımları eklendi.
- Projeye dönüş notları, toplu Git özeti, çalışan komutların PID/port/günlük takibi ve yeniden başlatma eklendi.
- Ortam kontrolü Node sürüm gereksinimlerini, eksik .env anahtarlarını ve Compose durumlarını inceler.
- Ctrl+Shift+F ile projeler arası içerik araması, .NET/JavaScript/Compose bağımlılık ve etki haritası, önizlemeli hazır/özel proje şablonları eklendi.

## Daha hızlı başlangıç

- İlk açılış rehberi geliştirme klasörünü seçmene, projeleri keşfetmene ve çalışma alanının genel bakışına geçmene yardımcı olur. Yardım → İlk açılış rehberi üzerinden tekrar açabilirsin.
- Yeni kurulumlar İngilizce başlar. Mevcut dil tercihleri korunur; dili Ayarlar bölümünden değiştirebilirsin.

## Çalışma düzenini paylaş

- Profil düzenleyicisinden başlatma profillerini dışa ve içe aktar. Hazır komutlar mevcut çalışma alanına yeniden bağlanır; içe aktarılan profiller inceleme için taslak olarak açılır ve otomatik çalıştırılmaz.
- Dosya menüsünden bağımsız bir HTML çalışma alanı raporu oluştur. Projeler varsayılan olarak anonimdir; istersen adları dahil et. Yollar, kaynak kodu, dallar ve uzak depo adresleri rapora alınmaz.

## Kontrolün sende olduğu geri bildirim

- Yardım → Hata bildir, uygulama sürümü, Windows sürümü ve dille birlikte düzenlenebilir bir GitHub issue taslağı açar. Günlük veya kaynak dosyası eklenmez; otomatik gönderim yapılmaz.
- İsteğe bağlı yerel özellik sayaçları varsayılan olarak kapalıdır. Yardım menüsünden görüntüle, dışa aktar veya temizle. Sayaçlar bilgisayarında kalır ve otomatik yüklenmez.

## Proje ve dağıtım

- DevWorkspace'in özgün kodu MIT lisansıyla sunuluyor; katkı rehberi ve üçüncü taraf bildirimleri eklendi.
- İmza doğrulama ve WinGet manifest oluşturucu, ileride imzalı dağıtım için hazırlandı. Güvenilir sertifika, yayımlanmış kurulum dosyası ve WinGet kabulü hâlâ gereklidir.
