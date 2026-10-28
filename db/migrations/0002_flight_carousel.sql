-- Göç 0002 (Faz 6, W3): bant numarası.
-- Bant ataması uçuş düzeyinde, AODB bağlamıyla gelir (POST /v1/flights/context). Değişebilir; son değer geçerlidir.
-- Biçim: 1-8 büyük harf ya da rakam (örn. '4', '07', 'B12'). NULL: henüz atanmadı.

ALTER TABLE flight_context ADD COLUMN carousel VARCHAR(8);
ALTER TABLE flight_context ADD CONSTRAINT flight_context_carousel_format CHECK (carousel ~ '^[A-Z0-9]{1,8}$');

-- flights.assigned_carousel ilk şemada vardı ama hiçbir kod yazmıyor ya da okumuyordu.
-- Bant numarasının tek kaynağı flight_context.carousel olsun diye kaldırılır.
ALTER TABLE flights DROP COLUMN assigned_carousel;
