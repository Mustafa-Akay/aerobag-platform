# AeroBag

Bagaj yolculuğu takibi, ETA/SLA tahmini ve (isteğe bağlı) çıkış doğrulama. Simülasyon verisiyle çalışan bir mühendislik portfolyo projesidir; gerçek kurum ya da yolcu verisi kullanılmaz.

Tasarım: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

## Gereksinimler
- .NET 10 SDK (LTS)
- Node 20+ (arayüz, sonraki adım)
- Docker (PostgreSQL ve Seq, sonraki adım)

## Çalıştırma
```
dotnet new sln -n AeroBag   # ilk seferde
dotnet sln add (ls -r src, tests *.csproj)
dotnet test
```

## Durum
Faz 1, adım 1: Domain durum makinesi, Type B ayrıştırıcı, olay eşleyici ve birim testleri.
