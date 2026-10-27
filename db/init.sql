-- AeroBag şeması (docs/ARCHITECTURE.md bölüm 7). Tüm veriler kurgusaldır.

CREATE TABLE flights (
  flight_id            VARCHAR(40) PRIMARY KEY,       -- örn. ZZ0124-20261022
  airline_code         VARCHAR(3)  NOT NULL,
  flight_number        VARCHAR(10) NOT NULL,
  flight_date          DATE        NOT NULL,
  aircraft_type        VARCHAR(10) NOT NULL,
  wide_body            BOOLEAN     NOT NULL DEFAULT FALSE,
  origin_airport       CHAR(3)     NOT NULL,
  destination_airport  CHAR(3)     NOT NULL,
  is_international     BOOLEAN     NOT NULL,
  parking_stand        VARCHAR(20),
  assigned_carousel    VARCHAR(20),
  ground_handler       VARCHAR(40),
  on_block_utc         TIMESTAMPTZ,
  total_bags           INT         NOT NULL DEFAULT 0,
  sla_first_bag_sec    INT         NOT NULL DEFAULT 900,   -- demo varsayımı
  sla_last_bag_sec     INT         NOT NULL DEFAULT 1800,  -- demo varsayımı
  first_bag_actual_sec INT,
  last_bag_actual_sec  INT,
  UNIQUE (flight_number, flight_date)
);

CREATE TABLE passengers (
  passenger_id  UUID PRIMARY KEY,
  flight_id     VARCHAR(40) NOT NULL REFERENCES flights(flight_id),
  lookup_key    BYTEA       NOT NULL,   -- HMAC-SHA256(gizli_anahtar, upper(pnr) || '|' || normalize(soyad))
  pnr_key       BYTEA       NOT NULL,   -- HMAC-SHA256(gizli_anahtar, 'PNR|' || upper(pnr)); çıkışta aynı PNR'daki yolcular (Faz 4)
  anonymized_at_utc TIMESTAMPTZ,        -- saklama süresi dolunca özetler rastgele değerle değiştirilir (KVKK notu)
  display_name  VARCHAR(64),            -- demoda takma ad; gerçek kurulumda boş
  cabin_class   CHAR(1)     NOT NULL,
  UNIQUE (flight_id, lookup_key)
);

CREATE TABLE bags (
  bag_id          UUID PRIMARY KEY,
  bag_tag_number  CHAR(10)    NOT NULL,
  flight_id       VARCHAR(40) NOT NULL REFERENCES flights(flight_id),
  passenger_id    UUID REFERENCES passengers(passenger_id),
  weight_kg       NUMERIC(5,2),
  current_state   VARCHAR(32) NOT NULL,
  state_version   INT         NOT NULL DEFAULT 0,
  eta_p10_utc     TIMESTAMPTZ,
  eta_p50_utc     TIMESTAMPTZ,
  eta_p90_utc     TIMESTAMPTZ,
  exit_cleared    BOOLEAN     NOT NULL DEFAULT FALSE,
  updated_at_utc  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (bag_tag_number, flight_id)    -- etiket numaraları zamanla yeniden kullanılabilir
);

CREATE TABLE baggage_events (
  event_id         UUID PRIMARY KEY,
  idempotency_key  VARCHAR(160) NOT NULL UNIQUE,   -- tekrar gelen mesajı reddeder
  bag_id           UUID NOT NULL REFERENCES bags(bag_id),
  message_type     VARCHAR(10)  NOT NULL,
  from_state       VARCHAR(32),
  to_state         VARCHAR(32)  NOT NULL,
  outcome          VARCHAR(16)  NOT NULL,          -- APPLIED, IGNORED_STALE, ANOMALY
  scanner_id       VARCHAR(32),
  source           VARCHAR(32)  NOT NULL,          -- imzası doğrulanan mesaj kaynağı (örn. simulator) ya da 'system'
  raw_text         TEXT,
  payload          JSONB        NOT NULL,
  occurred_at_utc  TIMESTAMPTZ  NOT NULL,
  received_at_utc  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_baggage_events_bag ON baggage_events(bag_id, occurred_at_utc);

CREATE TABLE outbox_messages (
  seq              BIGSERIAL   NOT NULL,          -- teslim sırası (created_at eşit olabilir)
  id               UUID PRIMARY KEY,
  topic            VARCHAR(64) NOT NULL,
  partition_key    VARCHAR(40) NOT NULL,
  payload          JSONB       NOT NULL,
  created_at_utc   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  attempts         INT         NOT NULL DEFAULT 0,
  processed_at_utc TIMESTAMPTZ
);
CREATE INDEX idx_outbox_pending ON outbox_messages(seq) WHERE processed_at_utc IS NULL;

CREATE TABLE exit_deadlines (
  bag_id      UUID PRIMARY KEY REFERENCES bags(bag_id),
  due_at_utc  TIMESTAMPTZ NOT NULL,
  status      VARCHAR(12) NOT NULL DEFAULT 'PENDING'    -- PENDING, CLEARED, OVERDUE, REVIEW
);
CREATE INDEX idx_exit_deadlines_due ON exit_deadlines(due_at_utc) WHERE status = 'PENDING';

CREATE TABLE dead_letter_messages (
  id              UUID PRIMARY KEY,
  source          VARCHAR(32) NOT NULL,
  raw_text        TEXT        NOT NULL,
  error           TEXT        NOT NULL,
  received_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- AODB'den gelen uçuş bağlamı (ML tahmini için). Gerçek kurulumda uçuş bilgi sisteminden beslenir.
CREATE TABLE flight_context (
  flight_id        VARCHAR(40) PRIMARY KEY,
  on_block_utc     TIMESTAMPTZ      NOT NULL,
  wide             BOOLEAN          NOT NULL,
  rain             BOOLEAN          NOT NULL,
  bags             INT              NOT NULL,
  stand_m          DOUBLE PRECISION NOT NULL,
  chute_m          DOUBLE PRECISION NOT NULL,
  speed_mps        DOUBLE PRECISION NOT NULL,
  arrivals_30      INT              NOT NULL,
  handler          VARCHAR(20)      NOT NULL,
  outcome_reported BOOLEAN          NOT NULL DEFAULT FALSE,
  received_at_utc  TIMESTAMPTZ      NOT NULL DEFAULT now()
);

-- ML servisine bildirilmiş bavul sonuçları (bavul düzeyi canlı düzeltme için; her bavul bir kez bildirilir).
CREATE TABLE ml_bag_outcomes (
  bag_id           UUID PRIMARY KEY REFERENCES bags(bag_id),
  reported_at_utc  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Faz 4.4: çıkış kapısı okumaları (ExitGuard). Her okuma, kararı ne olursa olsun buraya yazılır (denetim izi).
-- Sunulan PNR ve soyad SAKLANMAZ; yalnızca karar ve gerekçe tutulur.
CREATE TABLE gate_reads (
  read_id          UUID PRIMARY KEY,
  idempotency_key  VARCHAR(160) NOT NULL UNIQUE,
  gate_id          VARCHAR(32)  NOT NULL,
  bag_tag          CHAR(10)     NOT NULL,
  bag_id           UUID REFERENCES bags(bag_id),      -- bilinmeyen etikette boş
  decision         VARCHAR(20)  NOT NULL,             -- CLEARED_OWNER, REVIEW_MISMATCH, NO_DECISION, ALREADY_CLOSED, OUT_OF_SEQUENCE, UNKNOWN_TAG
  reason           VARCHAR(24)  NOT NULL,
  signal_quality   REAL,
  method           VARCHAR(32),
  source           VARCHAR(32)  NOT NULL,
  occurred_at_utc  TIMESTAMPTZ  NOT NULL,
  received_at_utc  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_gate_reads_bag ON gate_reads(bag_id);

-- Güvenlik personelinin inceleyeceği kayıtlar. Kapıda alarm yoktur; sonuç insan incelemesiyle verilir.
CREATE TABLE exit_reviews (
  review_id        UUID PRIMARY KEY,
  gate_read_id     UUID         NOT NULL UNIQUE REFERENCES gate_reads(read_id),
  bag_id           UUID REFERENCES bags(bag_id),
  bag_tag          CHAR(10)     NOT NULL,
  flight_id        VARCHAR(40),
  gate_id          VARCHAR(32)  NOT NULL,
  reason           VARCHAR(24)  NOT NULL,             -- PNR_MISMATCH, UNKNOWN_TAG, NOT_ON_CAROUSEL
  occurred_at_utc  TIMESTAMPTZ  NOT NULL,
  status           VARCHAR(12)  NOT NULL DEFAULT 'OPEN',   -- OPEN, RESOLVED
  resolution       VARCHAR(16),                        -- CONFIRMED, FALSE_ALARM
  resolved_by      VARCHAR(64),
  resolved_at_utc  TIMESTAMPTZ,
  note             VARCHAR(200)
);
CREATE INDEX idx_exit_reviews_open ON exit_reviews(occurred_at_utc) WHERE status = 'OPEN';

-- ---------------------------------------------------------------------------
-- Faz 4.3: uygulama rolü ve yalnızca eklemeli olay günlüğü.
-- API bu kısıtlı rolle bağlanır. Hiçbir tabloda DELETE yetkisi yoktur; olay günlüğü ve ölü mektup tablosu
-- yalnızca eklenebilir. Tablo sahibi (yönetici) de olay satırlarını tetikleyici yüzünden değiştiremez;
-- süper kullanıcı tetikleyiciyi kapatabilir, bu yüzden bu bir kurcalama engeli değil, kurcalamayı zorlaştıran
-- ve kazara değişikliği önleyen bir katmandır.
-- Parola yalnızca yerel demo içindir; gerçek kurulumda ayrı verilir.
-- ---------------------------------------------------------------------------
CREATE ROLE aerobag_app LOGIN PASSWORD 'aerobag_app_dev';

DO $$ BEGIN
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO aerobag_app', current_database());
END $$;

GRANT USAGE ON SCHEMA public TO aerobag_app;

GRANT SELECT, INSERT          ON flights, passengers, ml_bag_outcomes TO aerobag_app;
-- Saklama süresi işi yalnızca bu üç sütunu değiştirebilir (sütun düzeyi yetki).
GRANT UPDATE (lookup_key, pnr_key, anonymized_at_utc) ON passengers TO aerobag_app;
GRANT SELECT, INSERT, UPDATE  ON bags, outbox_messages, exit_deadlines, flight_context TO aerobag_app;
GRANT SELECT, INSERT          ON baggage_events TO aerobag_app;
GRANT INSERT                  ON dead_letter_messages TO aerobag_app;
GRANT SELECT, INSERT          ON gate_reads TO aerobag_app;
GRANT SELECT, INSERT, UPDATE  ON exit_reviews TO aerobag_app;
GRANT USAGE                   ON ALL SEQUENCES IN SCHEMA public TO aerobag_app;

CREATE FUNCTION forbid_event_log_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '% yalnızca eklemelidir; % engellendi', TG_TABLE_NAME, TG_OP
    USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE TRIGGER baggage_events_no_update_delete BEFORE UPDATE OR DELETE ON baggage_events
  FOR EACH ROW EXECUTE FUNCTION forbid_event_log_change();
CREATE TRIGGER baggage_events_no_truncate BEFORE TRUNCATE ON baggage_events
  FOR EACH STATEMENT EXECUTE FUNCTION forbid_event_log_change();
CREATE TRIGGER gate_reads_no_update_delete BEFORE UPDATE OR DELETE ON gate_reads
  FOR EACH ROW EXECUTE FUNCTION forbid_event_log_change();
CREATE TRIGGER gate_reads_no_truncate BEFORE TRUNCATE ON gate_reads
  FOR EACH STATEMENT EXECUTE FUNCTION forbid_event_log_change();
