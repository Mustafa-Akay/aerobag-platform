# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Depo dili Türkçedir: kod yorumları, belgeler, test adları dışındaki metinler ve commit mesajları Türkçe yazılır. Proje bir havayolunun bavul takip sisteminin **tek istasyon modülü** olarak konumlanan, tamamen simülasyon verisiyle çalışan bir portfolyo projesidir; gerçeklik/uyumluluk iddiası yapılmaz (bkz. `docs/ARCHITECTURE.md` §0.3 "İddia politikası"). Belgelere sonuç yazarken ölçülmeyeni ölçülmüş gibi sunma, olumsuz sonuçları ve sınırları da yaz.

## Komutlar

Gereksinimler: .NET 10 SDK, Docker Desktop, Node 20+, Python 3.12. Ortam Windows/PowerShell.

```powershell
scripts\demo                     # her şeyi açar (Postgres+ML, göç, API, UI, simülatör); scripts\demo -Stop kapatır
docker compose up -d --build     # Postgres 127.0.0.1:5433 + ML servisi 127.0.0.1:8000
dotnet run --project src\AeroBag.Core\AeroBag.Migrator             # şemayı kurar/günceller (API'den önce)
dotnet run --project src\AeroBag.Core\AeroBag.Migrator -- --status # göç durumu
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
cd src\aerobag-ui; npm run dev   # http://localhost:5173 (/v1 ve /hubs -> 5080 proxy)
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed 5   # --help: bütün seçenekler
```

Simülatörün API'siz değerlendirme modları: `--mode gate-eval`, `alert-eval`, `coverage-eval`; yük testi `--mode load` (API Release modunda, temiz veritabanı).

Derleme ve test (CI `.github/workflows/ci.yml` ile aynı):

```powershell
dotnet build --configuration Release
dotnet test                                              # Docker açık olmalı
dotnet test tests\AeroBag.UnitTests                      # yalnızca birim testleri (Docker gerekmez)
dotnet test --filter "FullyQualifiedName~TransferTests"  # tek sınıf / tek test
cd src\aerobag-ui; npm run build                         # tsc --noEmit + vite build (lint yok, tip kontrolü bu)
cd ml; python -m pytest -q tests                         # tek test: python -m pytest -q tests/test_service.py::test_adi
```

- `Directory.Build.props`: `TreatWarningsAsErrors=true`, nullable açık. Her uyarı derlemeyi kırar.
- Entegrasyon testleri (Testcontainers) her test sınıfı için ayrı bir `postgres:16-alpine` konteyneri başlatır ve göçleri uygular (`PostgresFixture`); paralellik 3 sınıfla sınırlı (`TestParallelism.cs`). `dotnet test` dakikalarca takılıyorsa önce `docker info`.
- API testleri `TestApi.Create(fx, ...)` ile gerçek API'yi kısıtlı `aerobag_app` rolüyle açar ve `TestApi.Signed(...)` ile HMAC imzalı istek üretir; yeni uçlar bu yolla test edilir.
- CI ayrıca NuGet / npm (`--omit=dev`) / pip-audit güvenlik denetimi yapar; açık içeren paket eklemek CI'ı kırar.

## Mimari (büyük resim)

Akış: Simülatör → (HMAC imzalı Type B metni, uçuş bağlamı, kapı paketleri) → API → ayrıştırıcı → durum makinesi → PostgreSQL (olay günlüğü + bavul durumu + outbox, tek işlemde) → outbox relay → SignalR (içeriksiz bildirim; istemci veriyi REST'ten yeniden çeker) → UI. ETA için API, ML servisine HTTP ile gider.

.NET katmanları (`src/AeroBag.Core`, çözüm `AeroBag.slnx`):
- **Domain**: `BagStateMachine` saf ve yan etkisizdir. Durumlar monoton ilerler: geride kalan olay `IGNORED_STALE` olarak günlüğe yazılır ama durumu geri almaz; atlanan ara adım anomali sayılır, ancak okuma kapsamı profilinde (`Coverage:Profile` = `full|typical|minimal`) okuyucusu olmayan noktalar atlanınca anomali sayılmaz.
- **Application**: saf kurallar (Type B ayrıştırıcı + `TypeBRedactor`, ETA, SLA, `BagAlertRules`, `ExitGuardRules`, `TransferPolicy`, `RequestSignature`, `OperationalClock`). Birim testleri buraya yöneliktir.
- **Infrastructure**: ham Npgsql (ORM yok). `BaggageEventProcessor` olay işlemenin merkezidir (tekilleştirme, durum geçişi, outbox yazımı aynı işlemde; bozuk mesaj `dead_letter_messages`'a). Sorgu sınıfları (`*Queries`), `GateService`, `RetentionService`, `OutboxRelay`, `AdvisoryLock` (arka plan işlerinin tek örnekte koşması).
- **Api**: tek dosyada minimal API (`Program.cs`, kapı uçları `GateEndpoints.cs`), JWT personel kimliği + yolcu jetonu (yalnızca kendi bavulları), imzalı alım (`Ingest.cs`, kaynak başına kapsam: `events,context,gate`), arka plan servisleri (`*BackgroundService.cs`: bavul uyarıları, çıkış süreleri, ML sonuç geri beslemesi, outbox, saklama süresi).
- **Simulator**: tohumlu üretici; hem canlı akışı hem ML eğitim CSV'lerini (`data/synthetic`) üretir. Model ve simülatör aynı üretici ailesinden olduğu için sonuçlar iyimser olabilir.

Önemli kesişen kurallar:
- **Saat**: `Clock:Mode=event` (varsayılan) iken operasyon saati en son olaydan türetilir (`EventDerivedClock`), duvar saati değil. Zamana bağlı mantıkta `TimeProvider` / operasyonel saat kullan, `DateTime.UtcNow` değil.
- **Şema**: `db/migrations/NNNN_ad.sql`, sırayla ve boşluksuz. **Uygulanmış göç dosyası asla değiştirilmez** (SHA-256 kontrolü göç aracını durdurur); değişiklik yeni dosyayla yapılır. Dosyalar `AeroBag.Infrastructure`'a gömülüdür. API şemayı değiştiremez, açılışta sürüm uyuşmazsa açılmaz. Uygulama rolünün hiçbir tabloda DELETE yetkisi yok, olay günlüğü yalnızca eklemeli (`AppendOnlyTests`); yeni tablo eklerken yetkileri göçte ver.
- **Kişisel veri**: ad, soyad, PNR düz metin saklanmaz ve loglanmaz; aramalar `LookupKeyHasher` (HMAC) özetiyle yapılır, ham Type B metni saklanmadan önce maskelenir. Tarayıcıda PNR/soyad saklanmaz, yalnızca sayfa belleğinde tutulur (sessionStorage'da yalnızca kısa ömürlü personel jetonu var, `auth.ts`). Ayrıntı `docs/THREAT_MODEL.md`, `docs/KVKK.md`.
- **Demo varsayılanları**: depodaki parola/anahtarlar yalnızca `Development` ortamında kabul edilir; üretimde eksik ayarla API açılmaz (`HardeningTests`). Yapılandırma anahtarları tablosu `docs/IMPLEMENTATION_STATUS.md` "Yapılandırma".
- **ML sözleşmesi**: `contracts/ml/*.json` API ile ML servisi arasındaki tek doğruluk kaynağıdır; iki taraf da buna karşı test edilir (`ml/tests/test_contract.py`, `tests/AeroBag.UnitTests/MlContractTests.cs`). Alan değişikliği önce bu dosyalarda yapılır. ML kapalıyken (`Ml:BaseUrl` boş) API ampirik ETA'ya düşer.
- **ML**: `ml/aerobag_ml` (kuantil LightGBM, q10/q50/q90), modeller `ml/models/*.txt` düz metin (pickle yok). Eğitim: `python -m aerobag_ml.train ../data/synthetic/train_A.csv models/`; değerlendirme `run_eval.py` / `run_bag_eval.py` (rejim A eğitim, rejim B test = dağılım kayması).
- **UI** (`src/aerobag-ui`): React 18 + Tailwind 4, router yok; sayfalar `pages/` altında. Metinler `i18n.tsx` (Türkçe/İngilizce) üzerinden; yeni metin iki dile de eklenir.

## Belge düzeni

Bir iş paketi bittiğinde genellikle şunlar güncellenir: `docs/CHANGELOG.md` (en üste; ne, neden, bilinen sınırlar, testler, toplam .NET test sayısı), `docs/IMPLEMENTATION_STATUS.md` (kodun bugünkü durumu, tasarımdan sapmalar), gerekirse `README.md` ve `README.en.md` birlikte (test sayısı README'de de geçer). `docs/ARCHITECTURE.md` hedef tasarımdır, koddan ayrışabilir. Paket adları T1..T8, W1..W12a gibi kodlarla anılır; dal adları `fazN/tK-kisa-ad` biçimindedir.
