## Çift tıklama ve güncelleme bildirimi düzeltmeleri

- Projeye çift tıklamada Git durumu yeniden okunur. Gerçekten değişiklik varsa diff açılır; temiz projeler ve Git deposu olmayan projeler normal şekilde açılır.
- Açılışta güncelleme kontrolü bekletilmeden başlar. Kontrol sürerken üstte durum bandı görünür; yeni sürüm bulunduğunda indirme ve yeniden başlatma durumuna dönüşür.
- Güncelleme kontrolü başarısız olursa bant yeniden deneme düğmesiyle görünür kalır. Açık uygulamada kontroller 15 dakikada bir, hata sonrası bir dakika içinde tekrarlanır.
