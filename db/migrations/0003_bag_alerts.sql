-- Göç 0003 (Faz 6, W4): kayıp ve geciken bavul tespiti.
-- Kayıp bavul bir durum değil, uyarıdır: durum makinesi monoton kalır, bavul sonradan okunursa uyarı "bulundu" diye kapanır.

-- Uçuşun operasyon kilometre taşları (AODB): kalkış ve varışta "indirme bitti". Zaman içinde gelir; ML özelliklerinden
-- (flight_context) ayrıdır ve onlardan önce de gelebilir. NULL: henüz bildirilmedi.
CREATE TABLE flight_milestones (
  flight_id                VARCHAR(40) PRIMARY KEY,
  departed_utc             TIMESTAMPTZ,
  unloading_completed_utc  TIMESTAMPTZ,
  updated_at_utc           TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Bavul uyarıları. Silinmez; kapanış bir kez yazılır (aşağıdaki tetikleyici). Kişisel veri içermez.
CREATE TABLE bag_alerts (
  alert_id       UUID PRIMARY KEY,
  bag_id         UUID        NOT NULL REFERENCES bags(bag_id),
  kind           VARCHAR(24) NOT NULL CHECK (kind IN ('NOT_LOADED', 'MISSING_AT_ARRIVAL')),
  opened_at_utc  TIMESTAMPTZ NOT NULL,   -- operasyon saati (IOperationalClock)
  closed_at_utc  TIMESTAMPTZ,
  close_reason   VARCHAR(16) CHECK (close_reason IN ('FOUND', 'OFFLOADED', 'CLEARED')),
  CHECK ((closed_at_utc IS NULL) = (close_reason IS NULL))
);
-- Bir bavulda aynı türden en fazla bir açık uyarı.
CREATE UNIQUE INDEX bag_alerts_one_open ON bag_alerts(bag_id, kind) WHERE closed_at_utc IS NULL;
CREATE INDEX bag_alerts_open ON bag_alerts(opened_at_utc) WHERE closed_at_utc IS NULL;

CREATE FUNCTION bag_alerts_close_once() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.closed_at_utc IS NOT NULL THEN
    RAISE EXCEPTION 'kapanmış uyarı değiştirilemez' USING ERRCODE = 'insufficient_privilege';
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER bag_alerts_close_once BEFORE UPDATE ON bag_alerts
  FOR EACH ROW EXECUTE FUNCTION bag_alerts_close_once();

-- Uçuşun bavullarını sayan sorgular için (gecikme bildirimi, operasyon panosu).
CREATE INDEX idx_bags_flight ON bags(flight_id);

GRANT SELECT, INSERT, UPDATE ON flight_milestones TO aerobag_app;
GRANT SELECT, INSERT         ON bag_alerts TO aerobag_app;
GRANT UPDATE (closed_at_utc, close_reason) ON bag_alerts TO aerobag_app;
