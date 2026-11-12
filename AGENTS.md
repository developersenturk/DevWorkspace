# DevWorkspace

- Derleme ve doğrulama için `Debug` yapılandırmasını kullan. Release paketi oluşturma.
- Kökteki `DevWorkspace.exe` için `dotnet publish src/DevWorkspace.csproj -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true` komutunu kullan. Tek dosyalı yayın ayarı WPF’nin yerel DLL’lerini pakete dahil eder. Yayınlanan EXE’yi köke kopyaladıktan sonra kökteki dosyadan ana pencerenin açıldığını doğrula.
