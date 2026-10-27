# AeroBag

![ci](https://github.com/Mustafa-Akay/aerobag-platform/actions/workflows/ci.yml/badge.svg)

Havalimanında bagaj yolculuğunu izleyen, yolcuya "bagajım ne zaman gelir" sorusunun cevabını bir tahmin aralığıyla veren ve operasyon ekibine uçuş bazlı özet sunan bir mühendislik projesi.

**Bu bir ürün değil, bir portfolyo projesidir.** Tüm veri simülasyondan gelir. Gerçek bir havalimanı, havayolu ya da yolcu verisi kullanılmaz ve IATA uyumluluğu iddia edilmez.

## Ne yapıyor

- Basitleştirilmiş Type B mesajlarını (BSM, BUM, BPM) sıkı sınırlarla ayrıştırır ve bagaj durum makinesinden geçirir. Durum sırası monotondur; atlanan adımlar "sıra dışı" olarak işaretlenir, geriden gelen mesajlar yok sayılır.
- Aynı mesajın tekrar gelmesini güvenle karşılar (idempotent), geçersiz mesajları ölü mektup tablosuna yazar.
- Yolcu, PNR ve soyadıyla bagajını sorgular. Ad, soyad ve PNR düz metin saklanmaz; yalnızca HMAC-SHA256 arama anahtarı tutulur. Kayıt yok, PNR yanlış ve soyad yanlış durumları aynı yanıtı verir, sorgu IP başına dakikada 20 ile sınırlıdır.
- Bagaj uçaktan indikten sonra banta çıkış süresini **aralık** olarak tahmin eder (P10 / P50 / P90). İndirmeden sonra tahmin, simülasyondaki geçmiş uçuşların gerçekleşen sürelerinden hesaplanır; yeterli geçmiş yoksa bunu açıkça belirtir. İniş öncesinde uçuşun özelliklerine bakan bir makine öğrenmesi modeli (kuantil LightGBM) uçuş düzeyinde aralık verir. İndirildikten sonra aynı modelin bavul düzeyi tahmini (canlı düzeltmeli), şutta ise mesafe ve bant hızından fizik tabanlı bir aralık gösterilir. ML servisi kapalıysa sistem eski tahmine döner.
- Operasyon panosu uçuş başına ilk ve son bavul teslim hedefini ve gecikme riskini gösterir (hedefler demo varsayımıdır, sözleşme değildir). Simülatör çıktısı kamuya açık referanslarla yalnızca büyüklük mertebesi açısından karşılaştırılmıştır; kalibre edilmemiştir (`docs/EVALUATION.md`).
- ML servisi (FastAPI): ilk bavul, ilk→son bavul süresi ve son bavul için P10/P50/P90 tahmini. Biten uçuşların gerçek süreleri servise geri bildirilir; servis bunlardan canlı düzeltme hesaplar ve yağmurlu/yavaş günlerdeki kaymayı yakalar. Değerlendirme, sonuçlar ve sınırlar: [docs/EVALUATION.md](docs/EVALUATION.md).
- Çıkış: kapıdaki okuma (`EXIT`) bavulu "çıkış okundu" yapar. Banttan alındıktan sonra 180 sn içinde okunmayan bavul, bir worker tarafından "çıkış okunmadı" (inceleme gerektiren durum, sonuç değil) olarak işaretlenir; sonradan okunursa kapanır. Yolcu eşleştirmesi yok, o ayrı bir aşamadır.
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

Simülatör ──uçuş bağlamı (AODB taklidi)──▶ POST /v1/flights/context ──▶ flight_context
API ◀──── P10/P50/P90 ────▶ ML servisi (FastAPI, ml/service)   ◀── biten uçuşların gerçek süreleri
```

Ayrıntılı tasarım: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Kodun tasarımdan ayrıştığı yerler ve bilinen eksikler: [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md).

## Çalıştırma

Gereksinimler: .NET 10 SDK, Docker Desktop, Node 20+.

```powershell
docker compose up -d --build                           # PostgreSQL (host portu 5433) + ML servisi (127.0.0.1:8000)
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
```

Arayüz (ayrı terminal):

```powershell
cd src\aerobag-ui
npm install
npm run dev                                            # http://localhost:5173
```

Operasyon panosu giriş ister. Demo kullanıcıları (yalnızca yerel demo): `ops` / `ops-demo-2026` ve `security` / `security-demo-2026`. Gerçek bir kurulumda kullanıcılar `Auth__Users__0__Username`, `Auth__Users__0__PasswordHash`, `Auth__Users__0__Role` ile, imza anahtarı `Auth__SigningKey` (en az 32 bayt) ile verilir. İmza anahtarı verilmezse API her açılışta rastgele bir anahtar üretir ve açık oturumlar yeniden başlatmada düşer.

Simülatör (ayrı terminal). Başlangıçta örnek PNR ve soyadları yazdırır, bunlarla yolcu sayfasında sorgu yapabilirsiniz:

```powershell
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed 5
```

Canlı modda çıkış okumaları ExitGuard kapı paketi olarak gönderilir (bavul-PNR eşleştirmesi); güvenlik rolüyle girince panoda inceleme kayıtları görünür. Kapı kuralının yanlış alarm oranını görmek için: `dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode gate-eval`.

`--speed` sanal saniye / gerçek saniye oranıdır. ETA bandını görmek için düşük bir değer (1 - 5) kullanın. Tüm seçenekler için simülatörü `--help` ile çalıştırın. Veri seti üretmek için `--mode csv` kullanılır.

Şema değiştiğinde veritabanı `docker compose down -v` ile sıfırlanır. Postgres hazır olmadan API'yi başlatmayın: hazır değilken gönderilen olaylar kaybolur ve sonraki olaylar "sıra dışı" görünür.

ML servisi çalışmıyorsa API yine açılır; iniş öncesi bavullarda tahmin gösterilmez. Servisi yeniden eğitmek ve değerlendirmeyi yeniden üretmek için [docs/EVALUATION.md](docs/EVALUATION.md) sonundaki komutlara bakın.

## Testler

```powershell
dotnet test
cd ml; python -m pytest -q tests                       # Python tarafı (Python 3.12, ml/requirements.txt)
```

Birim testleri Docker gerektirmez. Entegrasyon testleri Testcontainers ile gerçek bir PostgreSQL konteyneri başlatır, bu yüzden Docker açık olmalıdır. Her `push`'ta GitHub Actions .NET testlerini, Python testlerini ve arayüz derlemesini çalıştırır.

## Proje yapısı

| Klasör | İçerik |
|---|---|
| `src/AeroBag.Core/AeroBag.Domain` | Durum makinesi (saf, yan etkisiz) |
| `src/AeroBag.Core/AeroBag.Application` | Type B ayrıştırıcı, olay eşleyici, ETA hesabı |
| `src/AeroBag.Core/AeroBag.Infrastructure` | PostgreSQL erişimi, olay işleme, sorgular, outbox |
| `src/AeroBag.Core/AeroBag.Api` | Minimal API, SignalR hub'ı, arka plan servisi |
| `src/AeroBag.Core/AeroBag.Simulator` | Tohumlu, gürültü ayarlı bagaj akışı üretici |
| `src/aerobag-ui` | React + TypeScript + Tailwind arayüzü |
| `ml` | Python paketi (veri, modeller, değerlendirme) ve FastAPI tahmin servisi |
| `tests` | Birim ve entegrasyon testleri |
| `db/init.sql` | Veritabanı şeması |

## Sınırlar ve açık işler

- Sonuçlar yalnızca simülasyonla doğrulandı; model ve simülatör aynı üretici ailesinden geliyor, bu yüzden sonuçlar iyimser olabilir. Ölçümler uçuş düzeyindedir (`docs/EVALUATION.md`). İniş öncesi bavul düzeyindeki ML penceresi bir basitleştirmedir ve ayrıca ölçülmemiştir.
- Operasyon uçları (`/v1/flights`, `/v1/flights/sla`) ve canlı kanal JWT ister; yolcu jetonu yalnızca kendi bagajlarına erişir. Mesaj alma uçları (`/v1/events`, `/v1/flights/context`) yalnızca HMAC imzalı istek kabul eder; simülatör demo kaynağı (`simulator`) olarak imzalar. Gerçek kurulumda kaynaklar `Ingest__Sources__0__Id`, `Ingest__Sources__0__Secret` (en az 32 bayt), `Ingest__Sources__0__Scopes` (`events,context,gate`) ile verilir; simülatöre aynı anahtar `--source-secret` ya da `AEROBAG_SOURCE_SECRET` ile verilir.
- Depodaki gizli anahtar ve veritabanı parolaları geliştirme varsayılanlarıdır. API kısıtlı `aerobag_app` rolüyle bağlanır (silme yetkisi yok, olay günlüğü yalnızca eklemeli). Gerçek bir kurulumda ortam değişkeniyle (`Security__LookupKeySecret`, `ConnectionStrings__Default`) verilmelidir.
- Çıkış doğrulama (ExitGuard) PNR düzeyinde eşleştirir; yanlış alarm ve yakalama oranları yalnızca simülasyondaki varsayılan sensör hatalarıyla ölçüldü (`docs/EVALUATION.md`), sahada ölçülmedi. Süre dolumu sanal saatle çalışır (olay akışı durursa durur).
- Havalimanı bağlamı (gövde tipi, hava, mesafeler, yer hizmeti firması) gerçek bir AODB'den değil, simülatörden gelir. ML servisinin düzeltme durumu bellektedir ve yeniden başlayınca sıfırlanır; servis kimlik doğrulaması istemez, yalnızca yerel makineye açıktır.
- Bantta takılı bagaj yalnızca sayılır, durumu değiştirilmez.

Güvenlik ve gizlilik: [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md) (STRIDE, testler, açık kalanlar) ve [docs/KVKK.md](docs/KVKK.md) (kişisel verinin nereye gittiği, saklama süresi). Yol haritası: Faz 5 (isteğe bağlı RFID çıkış kapısı demosu).
