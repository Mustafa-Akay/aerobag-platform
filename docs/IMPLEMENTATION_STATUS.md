# AeroBag: Uygulama durumu (koda göre)

`ARCHITECTURE.md` hedef tasarımı anlatır. Bu belge **şu an çalışan kodu** özetler: ne çalışıyor, tasarımdan nerede ayrışıyor, ne bilinçli olarak yok. Çelişki olursa bu belge koda daha yakındır; kodla çelişirse kod geçerlidir. Her iş paketinin ayrıntılı anlatısı, bilinen sınırları ve testleri [CHANGELOG.md](CHANGELOG.md)'dedir.

Son güncelleme: 31 Ekim 2026 (toparlama, T2).

**Konum:** bir havayolunun bavul takip sisteminin tek istasyon modülü; yer hizmetleri ve havalimanı veri sağlayan taraf, ExitGuard isteğe bağlı havalimanı modülü (`ARCHITECTURE.md`, "Konumlandırma").

## Fazlar

| Faz | Durum |
|---|---|
| 1. Çekirdek | Tamam |
| 2. Makine öğrenmesi | Tamam (`EVALUATION.md`) |
| 3. Dayanıklılık | Ölçüt karşılandı; Kafka uyumlu broker ve Redis bilinçli olarak yok |
| 4. Güvenlik ve çıkış doğrulama | Tamam; `THREAT_MODEL.md`, `KVKK.md` |
| 5. Donanım demosu (RFID) | Başlanmadı, isteğe bağlı |
| 6. Sektör uyumu | Devam ediyor; paketler ve durum `REMEDIATION_PLAN.md` |

**31 Ekim kararı:** yeni özellik eklenmiyor. Faz 6'nın kod işi bitti; sıradaki iş toparlama: çerçeve (T1), kolay çalıştırma, demo senaryosu, README görselleri, tek sayfalık özet, gerçek veri planı. Ağa genişleme sektörden gelecek geri bildirime göre.

## Çalışan parçalar

| Parça | Durum |
|---|---|
| Type B ayrıştırıcı (BSM, BUM, BPM; `.I/.O` aktarma bağları; sıkı sınırlar) | Birim testli |
| Durum makinesi (monoton, `Rank` sırası, iki zincir: varış ve aktarma) | Birim testli |
| Olay işleme (idempotent, bavul başına kilit, ölü mektup, tek işlemde günlük + durum + outbox) | Entegrasyon testli |
| Yalnızca eklemeli olay günlüğü, kısıtlı uygulama rolü, ham metinde ad/PNR maskeleme | Entegrasyon testli |
| Şema göçü (`AeroBag.Migrator`, SHA-256 değişmezlik, sürüm uyuşmazsa API açılmaz) | Entegrasyon testli |
| Operasyon saati (`Clock:Mode=event|real`) | Birim + entegrasyon testli |
| Kimlik: personel JWT ve roller, yolcu jetonu (`etiket@uçuş`), HMAC imzalı mesaj kaynakları | Entegrasyon testli |
| Yolcu sorgusu (PNR + soyad, HMAC arama anahtarı, tekdüze 404, hız sınırı) | Entegrasyon testli |
| ETA aralığı (P10/P50/P90: `ML_FLIGHT`, `ML`, `PHYSICS`, `HISTORY`, `FALLBACK`) | Çalışıyor; ölçüm `EVALUATION.md` |
| SLA ve gecikme riski (ilk/son bavul, DEMO hedefleri, AODB'ye düşme) | Çalışıyor; olasılık kalibrasyonu ölçülmedi |
| Bant numarası (AODB bağlamından, değişince canlı bildirim) | Entegrasyon testli |
| Kayıp bavul uyarıları (`NOT_LOADED`, `MISSING_AT_ARRIVAL`, türetilmiş `ARRIVAL_DELAYED`) | Birim + entegrasyon testli, ölçüldü |
| Okuma kapsamı profilleri (`full`, `typical`, `minimal`) | Birim + entegrasyon testli, ölçüldü |
| Aktarma bavulu (iki bağlı satır, kabul kapanışı, kaçırılan bağlantı) | Birim + entegrasyon testli; risk uyarısı yok (W10b) |
| ExitGuard (kapı paketi, bavul–PNR eşleştirme, sessiz inceleme kaydı, güvenlik ekranı) | Bayraklı, isteğe bağlı modül; ölçüldü |
| Saklama süresi (teslimden 30 gün, teslim edilmeyende 90 gün üst sınır) | Entegrasyon testli |
| ML servisi (FastAPI, kuantil LightGBM, canlı düzeltme) ve sözleşme testi | Python + .NET testli |
| Outbox → SignalR içeriksiz canlı bildirim | Entegrasyon testli |
| React arayüzü: yolcu sayfası (TR/EN), operasyon panosu, güvenlik ekranı | Çalışıyor; arayüzde test çalıştırıcısı yok |
| Simülatör (tohumlu; canlı, CSV, ölçüm ve yük modları) | Birim testli |

Testler: 411 .NET testi (birim + Testcontainers ile gerçek PostgreSQL), Python ML testleri. Docker sağlıklıyken `dotnet test` bir geliştirici dizüstünde ~40 sn sürer.

## Tasarımdan ayrışan ve netleşen noktalar

- **Olay iletimi:** Kafka yerine Postgres outbox + SignalR (en az bir kez, `seq` sırası, 5 denemede bekletme). Tek örnek ve tek tüketici grubu varsayılır.
- **Okuma noktaları:** BPM okuma noktası `[A-Z]{3,10}\d{2}`; önek durumu belirler (`SEC`, `TRANSFER`, `LOAD`, `CHUTE`, `CAROUSEL`, `CLAIM`, `EXIT`). Bilinmeyen önek reddedilir.
- **Olay zamanı:** BSM'nin zamanı mesajda yoktur, alınma anı kullanılır; BUM ve BPM zamanı mesajdan gelir.
- **Kayıp bavul durum değil uyarıdır:** durum makinesi monoton kalır; bavul sonradan okunursa uyarı `FOUND` ile kapanır.
- **Bant uçuş düzeyindedir:** tek kaynak AODB bağlamıdır; BPM `.Q` alanı ayrıştırılır, kullanılmaz.
- **Okuma kapsamı yapılandırmayla verilir**, tahmin edilmez; API ve simülatör aynı profille çalışmalıdır.
- **Aktarma takibi bağlantı uçağına yüklemede biter;** varış tarafı başka bir havalimanının okuyucularındadır. Kart son adımda bavulun alınacağı havalimanını söyler.
- **ExitGuard PNR düzeyinde eşleştirir** (aynı rezervasyondakiler birbirinin bavulunu alabilir); belirsizde karar vermez, alarm yerine inceleme kaydı açar.
- **Canlı bildirim içeriksizdir:** istemci ayrıntıyı REST ile çeker; yoklama canlı bağlantıda 20 sn, yokken 3 sn, güvenlik ekranında 5 sn.

## Bilinen eksikler ve riskler

1. **Bütün sonuçlar simülasyondan.** Model ve simülatör aynı üretici aileden; sonuçlar iyimser olabilir. Gerçek havalimanı ya da havayolu verisi yok, IATA uyumluluğu iddia edilmez.
2. **ExitGuard oranları sensör varsayımlarına bağlı,** sahada ölçülmedi. Kapıya cihaz ve personel koymanın maliyeti kapsam dışı (W11).
3. **SLA olasılıklarının kalibrasyonu ölçülmedi;** `ARCHITECTURE.md` 6.2'deki kaynak artırımı önerisi (`suggestion`) uygulanmadı.
4. **Bantta takılı bavul tespiti sınırlı:** "alındı" okuması düşerse bavul `ON_CAROUSEL`'da kalır.
5. **Aktarma:** bağlantı kaçırma riski önceden tahmin edilmez (W10b sonraya bırakıldı).
6. **Kimlik ve anahtarlar:** TLS, anahtar kasası ve anahtar döndürme yok; yenileme jetonu ve jeton iptali yok; WebSocket jetonu sorgu parametresinde; jeton `sessionStorage`'da (HttpOnly çerez yok). Depodaki anahtar ve parolalar yalnızca yerel geliştirme içindir.
7. **Tek örnek varsayımı:** nonce önbelleği, ML düzeltme durumu ve outbox dağıtıcısı bellekte ya da tek örnekte çalışır.
8. **Değiştirilemezlik veritabanı düzeyinde:** süper kullanıcı tetikleyiciyi kapatabilir; hash zinciri ya da harici değiştirilemez depo yok.
9. **AODB bağlamı simülatörden** gelir (gövde tipi, hava, mesafeler, bant, kilometre taşları).
10. **Sabitler:** çıkış süresi 180 sn ve yapılandırılamaz; ML durumu son çağrının sonucudur, servis ayrıca yoklanmaz.
11. **Personel ekranları yalnızca Türkçe** (W9'un ikinci adımı).

## Yapılandırma

Ortam değişkenlerinde `:` yerine `__` kullanılır (örn. `Auth__SigningKey`).

| Anahtar | Varsayılan | Gerçek kurulumda |
|---|---|---|
| `ConnectionStrings:Default` | yerel `aerobag_app` rolü | kısıtlı uygulama rolüyle verilmeli |
| `Security:LookupKeySecret` | geliştirme değeri | **verilmeli** (HMAC arama anahtarı) |
| `Auth:SigningKey` | her açılışta rastgele | **verilmeli** (çok örnekte ortak) |
| `Auth:Users` | demo kullanıcılar | verilince demo kullanıcılar kapanır |
| `Ingest:Sources` | demo `simulator` kaynağı | kaynak başına anahtar ve kapsamlar; verilince demo kaynak kapanır |
| `Clock:Mode` | `event` | `real` |
| `Coverage:Profile` | `full` | kurulumdaki okuyucu yerleşimine göre |
| `Alerts:NotLoadedGraceMinutes` / `Alerts:MissingGraceMinutes` | 10 / 5 | saha verisiyle yeniden seçilmeli |
| `Transfer:CutoffMinutes` | 15 | havayolunun kabul kapanışı |
| `ExitGuard:Enabled` / `ExitGuard:MinSignalQuality` | `true` / 0,6 | modül kullanılmıyorsa `false` |
| `Ml:BaseUrl` / `Ml:TimeoutSeconds` | `http://127.0.0.1:8000` / 2 | boş verilirse ML kapalı |
| `Retention:PersonalDataDays` / `Retention:UndeliveredDays` | 30 / 90 | KVKK politikasına göre |

Simülatörün bütün seçenekleri: `dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --help`.

## Yerelde çalıştırma

Tek komutla: `scripts\demo` (açar), `scripts\demo -Stop` (kapatır). Elle:

```powershell
docker compose up -d --build                                                    # Postgres (5433) + ML (127.0.0.1:8000)
dotnet run --project src\AeroBag.Core\AeroBag.Migrator                          # şema (API'den önce)
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
cd src\aerobag-ui; npm install; npm run dev                                     # http://localhost:5173
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed 5
dotnet test                                                                     # Docker açık olmalı
```

- Şema değişince göç aracı yeniden çalıştırılır; veri korunur.
- API'yi Postgres hazır olduktan sonra başlatın; hazır değilken gönderilen olaylar kaybolur ve sonraki olaylar "sıra dışı" görünür.
- `dotnet test` birkaç dakikayı geçiyorsa önce Docker motorunun çalıştığını kontrol edin (`docker info`).
- Node `Fatal process out of memory: Zone` hatası verirse Windows'un bellek sınırı (Görev Yöneticisi > Bellek > "Tamamlanan") dolmuştur: açık süreçleri kapatın ya da yeniden başlatın; `%USERPROFILE%\.wslconfig` içinde `[wsl2]` altına `memory=4GB` yazarak Docker'ın WSL belleğini sınırlayın. `scripts\demo` bunu açılıştan önce denetler.
