-- Göç 0004 (Faz 6, W5): uçuş düzeyinde ilk ve son bavulun banda geliş zamanı (AODB).
-- Bant ağzında okuyucu olmayan kurulumlarda (Coverage:Profile=typical/minimal) ilk/son bavul SLA'sı bu zamanlardan
-- hesaplanır. Yer hizmeti bunları uçuş düzeyinde bildirir; bavul düzeyinde değildir. NULL: henüz bildirilmedi.

ALTER TABLE flight_milestones ADD COLUMN first_bag_on_belt_utc TIMESTAMPTZ;
ALTER TABLE flight_milestones ADD COLUMN last_bag_on_belt_utc  TIMESTAMPTZ;
ALTER TABLE flight_milestones ADD CONSTRAINT flight_milestones_belt_order
  CHECK (first_bag_on_belt_utc IS NULL OR last_bag_on_belt_utc IS NULL OR last_bag_on_belt_utc >= first_bag_on_belt_utc);
