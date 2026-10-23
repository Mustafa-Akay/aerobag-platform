# AeroBag

Bagaj yolculuğu takibi, ETA/SLA tahmini ve (isteğe bağlı) çıkış doğrulama. Simülasyon verisiyle çalışan bir mühendislik portfolyo projesidir; gerçek kurum ya da yolcu verisi kullanılmaz.

Tasarım: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

## Gereksinimler
- .NET 10 SDK (LTS)
- Docker Desktop (PostgreSQL ve entegrasyon testleri için)
- Node 20+ (arayüz, sonraki adım)

## Testler
```
dotnet test tests\AeroBag.UnitTests          # Docker gerekmez
dotnet test tests\AeroBag.IntegrationTests   # Docker gerekir (gerçek PostgreSQL konteyneri başlatır)
```

## API'yi çalıştırma
```
docker compose up -d
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
```
Simülatörle canlı akış (simülatör başlangıçta örnek PNR + soyad yazdırır):
```
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --flights 2 --bags 40 --speed 120
```
Yolcu sorgusu ve operasyon özeti:
```
Invoke-RestMethod "http://localhost:5080/v1/lookup?pnr=PNR&surname=SOYAD"
Invoke-RestMethod http://localhost:5080/v1/flights
```
Tek mesaj göndermek için (PowerShell):
```
$msg = "BSM`n.V/1LAYT`n.F/ZZ0124/22OCT/AYT/Y`n.N/0891458921001`n.W/K/1/18`n.P/YILMAZ/AYSE`n.L/AB3K7Q`nENDBSM"
Invoke-RestMethod -Method Post -Uri http://localhost:5080/v1/events -ContentType text/plain -Body $msg
Invoke-RestMethod http://localhost:5080/v1/bags/0891458921
```

## Durum
Faz 1: durum makinesi, Type B ayrıştırıcı, olay eşleyici, PostgreSQL kaydı (olay günlüğü, outbox, çıkış süresi, ölü mektup), `POST /v1/events`, `GET /v1/bags/{tag}`. Yolcu kaydı (HMAC anahtar), `GET /v1/lookup` (hız sınırlı), ETA taban çizgisi (geçmiş olaylardan P10/P50/P90), `GET /v1/flights`. Tek simülatör (canlı ve CSV). Sıradaki: React arayüzü, çıkış süresi arka plan işçisi.
