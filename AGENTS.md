# DevWorkspace

- Her yeni sürümde `src/Updates/ReleaseNotes.tr.md` ve `src/Updates/ReleaseNotes.en.md` dosyalarını o sürümün kullanıcıya dönük değişiklikleriyle güncelle. Bu notlar uygulamadaki Yenilikler penceresine gömülür; geçmiş yayınlar GitHub Releases üzerinden alınır.

- Derleme ve doğrulama için `Debug` yapılandırmasını kullan. Release paketi oluşturma.
- Arayüz doğrulamasında çalışan uygulamanın EXE yolunu ve sürümünü kontrol et. Kökteki taşınabilir EXE'nin güncellenmesi kurulu kopyayı güncellemez; ilgili pencereyi kullanıcının çalıştırdığı kopyada da doğrula.
- Kökteki `DevWorkspace.exe` için `dotnet publish src/DevWorkspace.csproj -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true` komutunu kullan. Tek dosyalı yayın ayarı WPF’nin yerel DLL’lerini pakete dahil eder. Yayınlanan EXE’yi köke kopyaladıktan sonra kökteki dosyadan ana pencerenin açıldığını doğrula.
