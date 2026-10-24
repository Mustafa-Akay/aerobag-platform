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
  status      VARCHAR(12) NOT NULL DEFAULT 'PENDING'    -- PENDING, CLEARED, OVERDUE
);
CREATE INDEX idx_exit_deadlines_due ON exit_deadlines(due_at_utc) WHERE status = 'PENDING';

CREATE TABLE dead_letter_messages (
  id              UUID PRIMARY KEY,
  source          VARCHAR(32) NOT NULL,
  raw_text        TEXT        NOT NULL,
  error           TEXT        NOT NULL,
  received_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
