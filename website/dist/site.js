const translations = {
skip:'İçeriğe geç', nav:'Nasıl çalışır?', source:'Kaynak kodu', eyebrow:'GELİŞTİRME KLASÖRÜNÜN ÖTESİNİ GÖR', title:'Birçok proje.<br><span>Tek bir net görünüm.</span>', intro:'Bir klasör daha arama, sıradaki işini bul. Projelerin, Git değişikliklerin ve geliştirme araçların tek bir Windows çalışma alanında.', download:'Windows için indir', requirements:'Windows x64 · Bağımsız kurulum dosyası', preview:'Gerçek çalışma alanın. Bütün resim.', caption:'Gerçek uygulama ve örnek depolar. Türkçe ve İngilizce arayüz seçenekleri mevcut.', stacks:'Farklı teknolojiler. Aynı çalışma alanı.', workflowLabel:'DAHA AZ ARAMA. DAHA ÇOK GELİŞTİRME.', workflowTitle:'Kodunla kaldığın<br>yerden devam et.', workflowIntro:'Projeler arasında geçerken ne değiştiğini anlamak ve yeniden işe koyulmak için bir çalışma tezgâhı.', discover:'Projelerini keşfet', discoverText:'Geliştirme klasörünü seç. Farklı teknolojilerin desteklenen proje dosyalarını keşfet, ilişkili bileşenleri grupla ve sonuçlarda ara.', inspect:'İlgilenmen gerekenleri gör', inspectText:'Çalışma alanının bağlamını kaybetmeden depo durumunu kontrol et, değişiklikleri ve geçmişi incele, desteklenen bağımlılık güvenliği bulgularını gözden geçir.', launch:'Geliştirmeye geri dön', launchText:'Geliştirme araçlarını aç, proje komutlarını çalıştır ve tekrarlanan işler için başlatma profillerini kaydet.', localLabel:'ÇALIŞMALARIN İÇİN MASAÜSTÜNDE BİR YER', localTitle:'Senin depoların.<br>Senin bilgisayarın.', localText:'Proje keşfi bilgisayarında gerçekleşir. Git, GitHub, güvenlik kontrolleri ve güncellemeler gerektiğinde kendi servislerini kullanır. İsteğe bağlı kullanım sayaçları yereldir ve varsayılan kapalıdır.', toolsText:'Git, SDK ve proje araçlarını sen getir. Kullanılabilir komutlar proje türüne ve yüklü araçlara bağlıdır.', privacy:'Gizlilik ve paylaşım rehberini oku', ready:'SIRADAKİ PROJENE YER AÇ', ctaTitle:'Hepsini bir araya getir.', portable:'Taşınabilir ZIP mi tercih edersin?', license:'Özgün kod: MIT. DevExpress ve diğer bağımlılıklar kendi koşullarına tabidir.', contribute:'Katkıda bulun', feedback:'Geri bildirim'
};
const nodes = [...document.querySelectorAll('[data-i18n]')];
const english = new Map(nodes.map(node=>[node,node.innerHTML]));
const toggle = document.getElementById('language');
toggle.addEventListener('click',()=>{
  const turkish = document.documentElement.lang !== 'tr';
  document.documentElement.lang = turkish ? 'tr' : 'en';
  for (const node of nodes) {
    // Both dictionaries are bundled constants; user input is never inserted.
    node.innerHTML = turkish ? translations[node.dataset.i18n] : english.get(node);
  }
  toggle.textContent = turkish ? 'English' : 'Türkçe';
  toggle.lang = turkish ? 'en' : 'tr';
  toggle.setAttribute('aria-label',turkish ? 'Switch to English' : 'Türkçeye geç');
  document.querySelector('nav').setAttribute('aria-label',turkish ? 'Ana menü' : 'Main');
  document.querySelector('img').alt = turkish ? 'Örnek depoları ve Git durumlarını Türkçe arayüzde gösteren DevWorkspace' : 'DevWorkspace showing sample repositories and Git status in the Turkish interface';
});
