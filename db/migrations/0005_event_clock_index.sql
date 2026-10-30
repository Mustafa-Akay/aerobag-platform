-- Göç 0005 (Faz 6, W7): operasyon saati sorgusu için kısmi indeks.
-- EventDerivedClock "şimdi"yi SELECT max(occurred_at_utc) FROM baggage_events WHERE message_type <> 'BSM' ile hesaplar.
-- İndeks olmadan bu sorgu tabloyu baştan sona tarıyordu (W7 ölçümü: ~183 bin olayda 23,7 ms; süre tabloyla doğrusal büyür).
-- Sorgu her yolcu sorgusunda, operasyon panosunda ve arka plan işlerinde 2-5 sn'de bir çalışır. Kısmi indeksle en büyük değer
-- indeksin sonundan okunur (tablo taranmaz).
CREATE INDEX idx_baggage_events_clock ON baggage_events (occurred_at_utc) WHERE message_type <> 'BSM';
