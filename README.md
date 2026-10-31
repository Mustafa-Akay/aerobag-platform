# AeroBag

![ci](https://github.com/Mustafa-Akay/aerobag-platform/actions/workflows/ci.yml/badge.svg)

Bir **havayolunun bavul takip sistemi** için tek istasyon modülü: bavulun check-in'den banda ya da bağlantı uçağına kadar yolculuğunu izler ve üç soruya cevap verir:

1. **Bu bavul uçağa bindi mi?** Yüklenmeyen ya da varışta indirilmeyen bavul, okuyucu kaçırmalarına karşı ölçülmüş bir uyarıyla personele ve yolcuya bildirilir.
2. **Aktarma bavulu bağlantıya yetişecek mi?** Gelen ve bağlantı uçuşundaki bavul birbirine bağlanır; kabul kapanışını kaçıranlar panoda, bağlantıyı kaçıranlar yolcu kartında görünür.
3. **Yolcu bavulunu ne zaman alacak?** Tek bir saat yerine bir **tahmin aralığı**; istasyon için ilk/son bavul teslim hedefi ve gecikme riski.

Neden havayolu: bavul kaybolduğunda sorumlu olan ve yolcunun başvurduğu taraf havayoludur, yolcu verisi (PNR) onun sistemindedir ve IATA Resolution 753 bavul takibini havayollarından ister. Yer hizmetleri firması ve havalimanı bu kurguda veri sağlayan taraftır. Çıkış kapısında bavul–PNR eşleştirmesi (ExitGuard) havalimanının kurabileceği **isteğe bağlı** bir modül olarak kodda durur.

> **Bu bir ürün değil, bir portfolyo projesidir.** Bütün veri simülasyondan gelir; gerçek havalimanı, havayolu ya da yolcu verisi kullanılmaz ve IATA uyumluluğu iddia edilmez. Sonuçlar ve sınırları [docs/EVALUATION.md](docs/EVALUATION.md)'de, ölçülmeyen ya da yapılmayan her şey [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md)'de açıkça yazılıdır.

| Yolcu: indirme öncesi (uçuş düzeyi ML aralığı) | Yolcu: indirildikten sonra (bavul düzeyi ML) |
|---|---|
| ![Yolcu sayfası, indirme öncesi](docs/images/passenger-before-unload.png) | ![Yolcu sayfası, bavul indirildi](docs/images/passenger-unloaded.png) |
| **Operasyon panosu ve teslim hedefi riski** | **Güvenlik ekranı: çıkış inceleme kayıtları** |
| ![Operasyon panosu](docs/images/ops-dashboard.png) | ![Güvenlik inceleme kayıtları](docs/images/security-reviews.png) |

## Öne çıkanlar

**Kayıp bavul uyarısı, okuyucu kaçırmasıyla birlikte ölçüldü.** Bavulun uçağa yüklenmediği ya da varışta indirilmediği bir durum değil uyarıdır; bavul sonradan okunursa uyarı "bulundu" diye kapanır. Simülasyonda yüklenmeyen bavulların hepsi uçak inmeden personel ekranına düşer. İlk ölçümde bu uyarıların ~5'te 4'ü kaçırılan bir yükleme okumasından doğan yanlış uyarıydı; bu yüzden yolcuya yalnızca varışta gösteriliyor. Sonuç: yolcuya giden yanlış uyarı 1000 bavulda 15,5'ten 1,8'e indi, gerçek vakaların %89'unda yolcu bant boşalmadan haberdar oluyor.

**Okuyucu olmayan yerde "bilmiyorum" diyebiliyor.** Çoğu istasyonda bant ağzında ve banttan almada okuyucu yoktur. Okuma kapsamı yapılandırmayla verilir (`full`, `typical`, `minimal`); sistem bu noktaların atlanmasını sıra dışı saymaz, SLA'yı yer hizmetinin bildirdiği zamanlara dayandırır ve yolcuya "büyük olasılıkla bantta" der. `typical` kurulumda kapsamı bilmeyen bir kural 1000 bavulda ~1058 "sıra dışı" sayardı (pratikte her bavul); kapsamı bilen kural ~58 sayıyor, yani yalnızca gerçekten kaybolan okumaları.

**Aralık tahmini, taban çizgisine karşı ölçüldü.** İndirmeden banta kalan süre için kuantil LightGBM, aynı rejimde ampirik yöntemin P50 hatasını 1,60 dakikadan 0,72 dakikaya indirir, aralığı 5,0 dakikadan 2,2 dakikaya daraltır ve kapsamayı hedefte (%79) tutar. Yağmurlu ve yavaş bir güne (rejim kayması) geçince model fazla emin kalır (kapsama %45). Biten bavulların gerçek sürelerinden beslenen canlı düzeltme kapsamayı %77'ye, P50 hatasını 1,0 dakikaya getirir. Bavula özgü özelliklerin katkı sağlamadığı gibi olumsuz sonuçlar da raporda.

**Güvenlik tasarımı tehdit modeliyle ve testlerle eşleştirildi.**
- **Kimlik doğrulama:** personel kısa ömürlü JWT ve rollerle giriyor. Yolcu jetonu yalnızca kendi bavullarını görüyor.
- **İmzalı kaynaklar:** mesaj kaynakları HMAC imzalı (zaman damgası, tek kullanımlık değer, gövde özeti).
- **Değiştirilemez günlük:** veritabanı rolünde hiçbir tabloda silme yetkisi yok, olay günlüğü yalnızca eklemeli.
- **Kişisel veri:** ad, soyad ve PNR hiçbir yerde düz metin saklanmıyor, saklama süresi dolunca arama özetleri de geri dönülmez şekilde siliniyor.
- **Bulunan açıklar:** geliştirme sırasında iki gizlilik açığı (ham mesajda ve sunucu kayıtlarında açıkta kalan ad ve PNR) bulundu, düzeltildi ve belgelendi.
- Her önlemin hangi testle doğrulandığı [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md)'de.

**Dayanıklı olay işleme.**
- **Durum makinesi:** Type B mesajları (BSM, BUM, BPM) sıkı sınırlarla ayrıştırılıp monoton bir durum makinesinden geçirilir.
- **Mesaj güvenliği:** çift gelen mesajlar tekilleştirilir, sırasız mesajlar durumu geri almaz, bozuk mesajlar ölü mektup tablosuna gider.
- **Canlı bildirim:** değişiklikler transactional outbox ile SignalR'a iletilir. Bildirimler içeriksizdir.

**İsteğe bağlı modül: çıkış doğrulama (ExitGuard), yanlış alarm maliyetiyle birlikte ölçüldü.** Kapıda okunan bavul ile sunulan PNR'ın özeti karşılaştırılır. Aynı rezervasyondaki yolcular birbirinin bavulunu alabilir, uyuşmazlık kapıda alarm değil sessiz bir inceleme kaydı üretir. Varsayılan sensör hatalarıyla yapılan 27.000 bavulluk simülasyonda vakaların %87'si yakalandı ve hiçbiri sahip diye geçmedi. Meşru okumaların %1,38'i yanlış alarma dönüştü. Vaka çok nadir olduğu için açılan incelemelerin yalnızca yaklaşık %24'ü gerçek (taban oran etkisi). Kapıda alarm yerine insan incelemesi seçilmesinin gerekçesi bu.

**411 .NET testi**, birim testleri ve Testcontainers ile gerçek PostgreSQL üzerinde çalışan entegrasyon testleri. Bunlara ek olarak ML tarafı için Python testleri var. Hepsi her `push`'ta GitHub Actions'ta çalışır.

## Mimari

```
 Simülatör (Type B, uçuş bağlamı, kapı paketleri) ──HMAC imzalı──┐
                                                                 ▼
 Yolcu sayfası ──PNR+soyad──▶  ASP.NET Core API  ──kısıtlı rol──▶ PostgreSQL
 Operasyon / güvenlik ──JWT──▶  (durum makinesi,              (olay günlüğü yalnızca eklemeli,
                                 ExitGuard, SLA)                bavul durumu, outbox, kapı okumaları)
          ▲                          │      │
          └── SignalR (içeriksiz) ◀──┘      └──HTTP──▶ ML servisi (FastAPI, kuantil LightGBM, canlı düzeltme)
```

| Katman | Teknoloji |
|---|---|
| API ve iş mantığı | .NET 10, ASP.NET Core minimal API, SignalR |
| Veri | PostgreSQL 16 (Npgsql), transactional outbox |
| Makine öğrenmesi | Python 3.12, LightGBM (kuantil regresyon), FastAPI |
| Arayüz | React, TypeScript, Tailwind, Vite |
| Test ve CI | xUnit, Testcontainers, pytest, GitHub Actions |

Ayrıntılı tasarım [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)'de.

## Çalıştırma

Gereksinimler: .NET 10 SDK, Docker Desktop, Node 20+.

**Tek komutla:**

```powershell
scripts\demo              # açar: Postgres + ML, göç, API, arayüz, simülatör (ayrı pencerelerde), tarayıcı
scripts\demo -Stop        # hepsini kapatır, arkada süreç bırakmaz (veri korunur)
```

Betik önce Docker motorunu, Windows'un ayırabileceği belleği ve 5080/5173 portlarını denetler; sonra servisleri sırayla açar ve her birinin hazır olmasını bekler. Seçenekler: `-Coverage typical|minimal`, `-Speed`, `-Seed`, `-NoSimulator`, `-NoBuild` (ayrıntı: `Get-Help scripts\demo.ps1 -Detailed`).

**Elle, adım adım:**

```powershell
docker compose up -d --build                                          # PostgreSQL (5433) + ML servisi (127.0.0.1:8000)
dotnet run --project src\AeroBag.Core\AeroBag.Migrator                # şemayı kurar / günceller (db/migrations)
dotnet run --project src\AeroBag.Core\AeroBag.Api --urls http://localhost:5080
cd src\aerobag-ui; npm install; npm run dev                           # ayrı terminal: http://localhost:5173
dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode live --speed 5   # ayrı terminal
```

- **Yolcu sayfası:** simülatör başlangıçta örnek PNR ve soyadları yazdırır, bunlarla yolcu sayfasında sorgu yapılır.
- **Operasyon panosu:** giriş ister. Demo kullanıcıları yalnızca yerel demo içindir: `ops` / `ops-demo-2026` ve `security` / `security-demo-2026`. Güvenlik rolüyle girince çıkış inceleme kayıtları görünür.
- **ExitGuard değerlendirmesi:** `dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode gate-eval` komutu kuralın sinyal eşiğine göre yanlış alarm ve yakalama oranlarını API'siz hesaplar.
- **Kayıp bavul uyarısı değerlendirmesi:** `dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode alert-eval` komutu yüklenmeyen ve varışta bulunmayan bavulların yakalanma oranını, tespit gecikmesini ve okuma kaybından doğan yanlış uyarıları tolerans süresine göre API'siz hesaplar.
- **Yük testi:** API Release modunda açıkken `dotnet run -c Release --project src\AeroBag.Core\AeroBag.Simulator -- --mode load` imzalı mesajları eşzamanlı gönderir; saniyedeki mesaj sayısını ve gecikme yüzdeliklerini raporlar. Temiz bir veritabanında çalıştırın.
- **Okuma kapsamı değerlendirmesi:** `dotnet run --project src\AeroBag.Core\AeroBag.Simulator -- --mode coverage-eval` komutu `full`, `typical` ve `minimal` profillerinde yolcunun bilgilendirilme gecikmesini, SLA'nın kaynağını ve "sıra dışı" sayısını API'siz karşılaştırır.
- **Okuma kapsamı:** varsayılan olarak her noktada okuyucu olduğu varsayılır. Bant ağzında ve banttan almada okuyucu olmayan yaygın bir kurulumu denemek için API'yi `$env:Coverage__Profile="typical"` ile, simülatörü `--coverage typical` ile açın (`minimal`: yalnızca yükleme ve indirme). İkisi aynı profille çalışmalıdır.
- **Aktarma bavulu:** canlı modda bavulların ~%6'sı aktarma bavuludur (`--transfer-rate`, demo varsayımı): banda değil, buradan kalkan bir bağlantı uçuşuna (ZZ5xxx) gider. Yolcu kartı iki uçuşu tek yolculuk olarak gösterir; operasyon panosunda "Aktarma bavulları" bölümü bağlantı uçuşu başına sayaçları ve bavul kabul kapanışını (`Transfer__CutoffMinutes`, varsayılan 15) gösterir.
- **Şema değişince:** göç aracını yeniden çalıştırın; veri korunur. API, veritabanı kodun beklediği sürümde değilse açılmaz ve ne yapılacağını söyler.

Gerçek bir kurulumda verilmesi gereken anahtarlar (`Auth__SigningKey`, `Ingest__Sources__*`, `Security__LookupKeySecret`, `ConnectionStrings__Default`) ve simülatörün bütün seçenekleri (`--help`) için [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md)'ye bakın.

## Testler

```powershell
dotnet test                              # Docker açık olmalı (entegrasyon testleri gerçek PostgreSQL başlatır)
cd ml; python -m pytest -q tests         # Python 3.12, ml/requirements.txt
```

## Belgeler

| Belge | İçerik |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Hedef tasarım: fazlar, veri akışı, durum makinesi, sözleşmeler |
| [IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md) | Kodun şu anki durumu, tasarımdan sapmalar, bilinen eksikler, yapılandırma |
| [CHANGELOG.md](docs/CHANGELOG.md) | Her iş paketinin anlatısı, sınırları ve testleri (en yeni en üstte) |
| [EVALUATION.md](docs/EVALUATION.md) | ML ve ExitGuard ölçümleri, olumsuz sonuçlar, simülatörün kamuya açık referanslarla karşılaştırması |
| [THREAT_MODEL.md](docs/THREAT_MODEL.md) | STRIDE: her tehdit için önlem, test ve açık kalanlar |
| [KVKK.md](docs/KVKK.md) | Kişisel verinin nereye gittiği, saklama süresi (hukuki danışmanlık değildir) |

## Proje yapısı

| Klasör | İçerik |
|---|---|
| `src/AeroBag.Core/AeroBag.Domain` | Durum makinesi (saf, yan etkisiz) |
| `src/AeroBag.Core/AeroBag.Application` | Type B ayrıştırıcı ve maskeleme, ETA ve SLA hesabı, ExitGuard kuralı, istek imzası |
| `src/AeroBag.Core/AeroBag.Infrastructure` | PostgreSQL erişimi, olay işleme, kapı servisi, sorgular, outbox, saklama süresi |
| `src/AeroBag.Core/AeroBag.Api` | Minimal API, kimlik doğrulama, imza doğrulama, SignalR, arka plan işleri |
| `src/AeroBag.Core/AeroBag.Simulator` | Tohumlu bavul akışı ve kapı sensörü modeli, ExitGuard değerlendirmesi |
| `src/aerobag-ui` | Yolcu sayfası (Türkçe/İngilizce), operasyon panosu, güvenlik ekranı |
| `ml` | Veri, modeller, değerlendirme ve FastAPI tahmin servisi |
| `contracts/ml` | API ile ML servisi arasındaki istek/yanıt sözleşmesi (iki taraf da buna karşı test edilir) |
| `tests` | Birim ve entegrasyon testleri |
| `db/migrations` | Sırayla uygulanan şema göçleri (şema, uygulama rolü, yetkiler) |
| `src/AeroBag.Core/AeroBag.Migrator` | Göç aracı (tablo sahibi hesabıyla çalışır) |

## Sınırlar

- **Tek istasyon:** bu sürüm bir havayolunun tek istasyondaki görünümüdür. Ağa genişleme (hub ve istasyonlar, istasyon başına okuma kapsamı, aktarma bavulunun varış tarafı) sektörden gelecek geri bildirime göre yapılacak.
- **Simülasyon:** sonuçların hepsi simülasyondan geliyor. Model ile simülatör aynı üretici ailesinden olduğu için sonuçlar iyimser olabilir. Simülatör kamuya açık referanslarla yalnızca büyüklük mertebesi olarak karşılaştırıldı, kalibre edilmedi.
- **ExitGuard oranları:** benim koyduğum sensör varsayımlarına bağlı, sahada ölçülmedi.
- **Yerel kurulum:** TLS, anahtar kasası, anahtar döndürme ve çok örnekli kurulum kapsam dışı. Depodaki anahtarlar ve parolalar geliştirme varsayılanlarıdır.
- **Uçuş bağlamı:** gövde tipi, hava ve mesafeler gibi bilgiler gerçek bir uçuş bilgi sisteminden değil, simülatörden geliyor.
- **Faz 5:** RFID çıkış kapısı donanım demosu isteğe bağlıdır ve yapılmadı.
