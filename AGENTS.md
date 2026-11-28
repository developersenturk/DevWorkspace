# DevWorkspace

- Her yeni sürümde `src/Updates/ReleaseNotes.tr.md` ve `src/Updates/ReleaseNotes.en.md` dosyalarını o sürümün kullanıcıya dönük değişiklikleriyle güncelle. Bu notlar uygulamadaki Yenilikler penceresine gömülür; geçmiş yayınlar GitHub Releases üzerinden alınır.
- Sürüm notlarını genel ürün davranışı üzerinden yaz; belirli müşteri, uygulama veya proje adlarıyla sınırlı açıklamalar kullanma.

- Derleme ve doğrulama için `Debug` yapılandırmasını kullan. Release paketi oluşturma.
- Kullanıcı açıkça talimat vermeden kurulu uygulamayı güncelleme veya kurulu kopyanın dosyalarını değiştirme. Güncel EXE'yi kökte hazırla ve kullanıcıya sun; kurulu kopyayı kullanıcı günceller. Düzeltme, derleme, doğrulama, commit veya push isteği kurulu uygulamayı güncelleme izni değildir.
- Arayüz doğrulamasında çalışan uygulamanın EXE yolunu ve sürümünü kontrol et. Kökteki taşınabilir EXE'nin güncellenmesi kurulu kopyayı güncellemez. Yeni davranışı kökteki EXE'de doğrula; kurulu kopyada doğrulama gerekirse sürümünü kontrol et ve açık güncelleme talimatı olmadan dosyalarını değiştirme.
- Kökteki `DevWorkspace.exe` için `dotnet publish src/DevWorkspace.csproj -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true` komutunu kullan. Tek dosyalı yayın ayarı WPF’nin yerel DLL’lerini pakete dahil eder. Yayınlanan EXE’yi köke kopyaladıktan sonra kökteki dosyadan ana pencerenin açıldığını doğrula.
