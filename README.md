# AeroBag

![ci](https://github.com/Mustafa-Akay/aerobag-platform/actions/workflows/ci.yml/badge.svg)

Havalimanında bagaj yolculuğunu izleyen, yolcuya "bagajım ne zaman gelir" sorusunun cevabını bir tahmin aralığıyla veren ve operasyon ekibine uçuş bazlı özet sunan bir mühendislik projesi.

**Bu bir ürün değil, bir portfolyo projesidir.** Tüm veri simülasyondan gelir. Gerçek bir havalimanı, havayolu ya da yolcu verisi kullanılmaz ve IATA uyumluluğu iddia edilmez.

## Ne yapıyor

- Basitleştirilmiş Type B mesajlarını (BSM, BUM, BPM) sıkı sınırlarla ayrıştırır ve bagaj durum makinesinden geçirir. Durum sırası monotondur; atlanan adımlar "sıra dışı" olarak işaretlenir, geriden gelen mesajlar yok sayılır.
- Aynı mesajın tekrar gelmesini güvenle karşılar (idempotent), geçersiz mesajları ölü mektup tablosuna yazar.
- Yolcu, PNR ve soyadıyla bagajını sorgular. Ad, soyad ve PNR düz metin saklanmaz; yalnızca HMAC-SHA256 arama anahtarı tutulur. Kayıt yok, PNR yanlış ve soyad yanlış durumları aynı yanıtı verir, sorgu IP başına dakikada 20 ile sınırlıdır.
- Bagaj uçaktan indikten sonra banta çıkış süresini **aralık** olarak tahmin eder (P10 / P50 / P90). Tahmin, simülasyondaki geçmiş uçuşların gerçekleşen sürelerinden hesaplanır. Yeterli geçmiş yoksa bunu açıkça belirtir.
- Operasyon panosu: uçuş başına bagaj sayıları, ilk bagaj süresi, sıra dışı olaylar ve 20 dakikadan uzun süredir bantta bekleyen bagajlar.
- Değişiklikler outbox üzerinden SignalR ile canlı iletilir. Bildirimler içeriksizdir ("bu bagaj değişti"), ayrıntıyı istemci yeniden çeker. Canlı bağlantı yoksa arayüz yoklamaya döner.

## Mimari (özet)

```
Simülatör ──Type B metni──▶ POST /v1/events
                               │  ayrıştır → eşle → durum makinesi
                               ▼
                         PostgreSQL (tek işlemde: olay günlüğü + bagaj durumu + outbox)
                               │
                  Outbox dispatcher (arka plan) ──▶ SignalR /hubs/live
                               │                          │
              GET /v1/lookup, /v1/flights ◀───────────────┘
                               ▲
                   React arayüzü (yolcu sayfası, operasyon panosu)
```

Ayrıntılı tasarım: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Kodun tasarımdan ayrıştığı yerler ve bilinen eksikler: [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md).

## Çalıştırma

Gereksinimler: .NET 10 SDK, Docker Desktop, Node 20+.

```powershell
docker compose up -d                                   # PostgreSQL (host portu 5433)
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
```

Arayüz (ayrı terminal):

```powershell
cd src\aerobag-ui
npm install
npm run dev                                            # http://localhost:5173
```

Simülatör (ayrı terminal). Başlangıçta örnek PNR ve soyadları yazdırır, bunlarla yolcu sayfasında sorgu yapabilirsiniz:

```powershell
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed 5
```

`--speed` sanal saniye / gerçek saniye oranıdır. ETA bandını görmek için düşük bir değer (1 - 5) kullanın. Tüm seçenekler için simülatörü `--help` ile çalıştırın. Veri seti üretmek için `--mode csv` kullanılır.

Şema değiştiğinde veritabanı `docker compose down -v` ile sıfırlanır. Postgres hazır olmadan API'yi başlatmayın: hazır değilken gönderilen olaylar kaybolur ve sonraki olaylar "sıra dışı" görünür.

## Testler

```powershell
dotnet test
```

Birim testleri Docker gerektirmez. Entegrasyon testleri Testcontainers ile gerçek bir PostgreSQL konteyneri başlatır, bu yüzden Docker açık olmalıdır. Her `push`'ta GitHub Actions aynı testleri ve arayüz derlemesini çalıştırır.

## Proje yapısı

| Klasör | İçerik |
|---|---|
| `src/AeroBag.Core/AeroBag.Domain` | Durum makinesi (saf, yan etkisiz) |
| `src/AeroBag.Core/AeroBag.Application` | Type B ayrıştırıcı, olay eşleyici, ETA hesabı |
| `src/AeroBag.Core/AeroBag.Infrastructure` | PostgreSQL erişimi, olay işleme, sorgular, outbox |
| `src/AeroBag.Core/AeroBag.Api` | Minimal API, SignalR hub'ı, arka plan servisi |
| `src/AeroBag.Core/AeroBag.Simulator` | Tohumlu, gürültü ayarlı bagaj akışı üretici |
| `src/aerobag-ui` | React + TypeScript + Tailwind arayüzü |
| `tests` | Birim ve entegrasyon testleri |
| `db/init.sql` | Veritabanı şeması |

## Sınırlar ve açık işler

- Sonuçlar yalnızca simülasyonla doğrulandı. ETA taban çizgisinin doğruluk ve kapsama ölçümü henüz yapılmadı.
- `/v1/events`, `/v1/flights` ve `/hubs/live` uçlarında kimlik doğrulama yok; yalnızca yerel demo içindir.
- Depodaki gizli anahtar ve veritabanı parolası geliştirme varsayılanlarıdır. Gerçek bir kurulumda ortam değişkeniyle (`Security__LookupKeySecret`, `ConnectionStrings__Default`) verilmelidir.
- Çıkış doğrulama (bagaj-yolcu eşleştirme) henüz yok. Çıkış süresi kayıtları tutuluyor, ama bu kayıtları temizleyen bir okuma ve süresi dolanları işleyen bir worker yok.
- Uçak tipi, park mesafesi, hava durumu ve bant numarası gibi havalimanı bağlamı henüz alınmıyor. Makine öğrenmesi aşamasından önce gerekir.
- Bantta takılı bagaj yalnızca sayılır, durumu değiştirilmez.

Yol haritası: bağlam verisi girişi, ETA doğruluk ölçümü ve kuantil modeli, çıkış doğrulama modülü.
