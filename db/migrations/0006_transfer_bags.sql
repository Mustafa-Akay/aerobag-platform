-- Göç 0006 (Faz 6, W10a): aktarma bavulu.
-- Aktarma bavulu iki bağlı satırla tutulur; her satır yine (etiket, uçuş) ile tanınır:
--   gelen uçuştaki satır  : onward_flight_id = bağlantı uçuşu (BSM .O satırından ya da aktarma BSM'sinden)
--   bağlantı uçuşundaki satır: inbound_flight_id = gelen uçuş (aktarma BSM'sinin .I satırından)
-- Gelen uçuştaki satır indirildikten sonra TRANSFERRED ile kapanır; bağlantı satırı TRANSFER_SORTED ve
-- LOADED_ON_AIRCRAFT adımlarından geçer. NULL: aktarma değil (ya da henüz bilinmiyor).

ALTER TABLE bags ADD COLUMN inbound_flight_id VARCHAR(40);
ALTER TABLE bags ADD COLUMN onward_flight_id  VARCHAR(40);
ALTER TABLE bags ADD CONSTRAINT bags_transfer_links
  CHECK ((inbound_flight_id IS NULL OR inbound_flight_id <> flight_id)
     AND (onward_flight_id  IS NULL OR onward_flight_id  <> flight_id));

-- Bağlantı uçuşu panosu (onward satırlar uçuşa göre) ve gelen satırdan bağlantı satırına geçiş için.
CREATE INDEX idx_bags_inbound ON bags (inbound_flight_id) WHERE inbound_flight_id IS NOT NULL;
CREATE INDEX idx_bags_onward  ON bags (onward_flight_id)  WHERE onward_flight_id  IS NOT NULL;

-- Bağlantı uçuşunun planlanan kalkışı (AODB, POST /v1/flights/milestones). Bavul kabul kapanışı bundan hesaplanır
-- (Transfer:CutoffMinutes, demo varsayımı). NULL: bildirilmedi.
ALTER TABLE flight_milestones ADD COLUMN scheduled_departure_utc TIMESTAMPTZ;
