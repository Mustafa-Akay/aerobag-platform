# AeroBag: Kişisel veri ve KVKK notu

**Bu belge hukuki danışmanlık değildir.** AeroBag simülasyon verisiyle çalışan bir mühendislik projesidir; demodaki bütün yolcu, uçuş ve rezervasyon verisi sahtedir. Gerçek bir kurulumda aydınlatma metni, açık rıza gereksinimi, veri sorumlusu ve işleyen rolleri, saklama süreleri ve yurt dışına aktarım kurumun politikasına ve hukuk danışmanlığına tabidir. Bu belge, sistemin kişisel veriyi teknik olarak nasıl ele aldığını anlatır.

## 1. Hangi veri nereye gider

| Veri | Nereden gelir | Ne saklanır | Ne saklanmaz |
|---|---|---|---|
| Yolcu adı ve soyadı | BSM mesajı (`.P/`) | Hiçbir şey; yalnızca PNR ile birlikte HMAC özeti (`lookup_key`) | Düz metin ad/soyad (ham mesaj metninde de maskelenir) |
| PNR | BSM mesajı (`.L/`) | HMAC özetleri: `lookup_key` (PNR + soyad) ve `pnr_key` (yalnızca PNR, ayrı önekle) | Düz metin PNR |
| Yolcunun sorguda girdiği PNR ve soyad | Yolcu sayfası (`POST` gövdesi) | Hiçbir şey; yalnızca bellekte özetlenir | Sunucu istek kayıtlarında da yer almaz (adres kayıtları kapatıldı) |
| Kapıda sunulan PNR | Kapı sensörü paketi | Hiçbir şey; bellekte özetlenip karşılaştırılır | Kapı okuması ve inceleme kayıtlarında yoktur (testle doğrulanır) |
| Bavul etiketi, uçuş, olay zamanları | Type B mesajları | Saklanır | Doğrudan kişiyi tanımlamaz; ama arama özetiyle bağlıyken kişisel veriyle ilişkilidir |
| Personel kullanıcı adı | Giriş | İnceleme kaydını kimin kapattığı (`resolved_by`) | Parola (yalnızca PBKDF2 özeti, yapılandırmada) |
| Jetonlar | API | Saklanmaz (kısa ömürlü, imzalı) | Yolcu jetonunda ad ve PNR yoktur, yalnızca bavul etiketleri |

## 2. Uygulanan ilkeler

- **Veri azaltma:** arama için gereken tek şey HMAC özetidir; ad, soyad ve PNR hiçbir tabloda tutulmaz. Gizli anahtar veritabanında değil, uygulama yapılandırmasındadır (`Security__LookupKeySecret`).
- **Amaçla sınırlılık:** yolcu yalnızca kendi bavullarını görür; personel bavul ve uçuş verisini görür ama yolcu kimliğini göremez (veritabanında yoktur).
- **Saklama süresi:** bir yolcunun bütün bavulları teslim edildikten (banttan alındı ya da çıkış durumunda) ve son güncellemeden 30 gün sonra arama özetleri rastgele değerle değiştirilir (`Retention:PersonalDataDays`). Teslim durumuna hiç ulaşmayan bavulların yolcuları için (kayıp bavul ya da bant ağzında okuyucu olmayan kurulum, W5) üst sınır son güncellemeden 90 gündür (`Retention:UndeliveredDays`). Bu sınır olmasa böyle yolcuların arama özetleri hiç silinmezdi. Aktarma yolcusunun (W10a) iki uçuşta ayrı yolcu kaydı vardır; gelen uçuştaki kayıt bavul aktarmaya devredilince, bağlantı uçuşundaki kayıt bavul bağlantı uçağına yüklenince teslim edilmiş sayılır (bu havalimanındaki iş biter). Bundan sonra gizli anahtarı bilen biri bile PNR ve soyaddan o kayda ulaşamaz. Bavul ve olay kayıtları kişisel veri içermediği için operasyon istatistiği amacıyla kalır. İş saatte bir çalışır, gerçek saati kullanır.
- **Erişim kontrolü ve en az yetki:** rol tabanlı yetki (yolcu, operasyon, güvenlik); veritabanı rolünün silme yetkisi yoktur ve yolcu satırında yalnızca özet sütunlarını değiştirebilir (`THREAT_MODEL.md`).
- **Kayıtlarda kişisel veri yok:** ham mesaj metni ve ölü mektuplar maskelenir; istek adresleri kayda geçmez; canlı kanal içeriksizdir.

## 3. Açık kalanlar ve gerçek kurulumda yapılması gerekenler

- Saklama süresi (30 gün) bir demo varsayımıdır; gerçek süre kurumun politikasıyla belirlenmelidir.
- Olay günlüğü yalnızca eklemeli olduğu için olay satırları silinmez. Olay satırları kişisel veri içermez (ad ve PNR maskelenir), ama bu tasarım "silme hakkı" taleplerinin olay günlüğü dışında ele alınmasını gerektirir. Özetler değiştirildikten sonra olaylar kişiyle ilişkilendirilemez.
- Bu değişiklikten önce oluşturulmuş veritabanlarında ham metinler maskelenmemiş olabilir (demo verisi; `docker compose down -v` ile sıfırlanır).
- Aydınlatma metni, rıza akışı, ilgili kişi başvuru süreci ve veri işleme envanteri bu projede yoktur.
- Yedekler, log toplama sistemleri ve ters vekil sunucular kapsam dışıdır; gerçek kurulumda bunlar da aynı kurallara bağlanmalıdır.
