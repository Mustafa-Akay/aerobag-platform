import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import type { BagNotice, EtaView } from "./api";

/**
 * Yolcu ekranı için TR/EN sözlüğü (W9). Personel ekranları şimdilik yalnızca Türkçedir.
 * İngilizce sözlük Türkçe sözlüğün tipini taşır: bir anahtar eksik ya da fazla olursa derleme hata verir.
 */
export type Lang = "tr" | "en";

export type StepState =
  | "CHECKED_IN"
  | "SECURITY_SCREENED"
  | "LOADED_ON_AIRCRAFT"
  | "UNLOADED_AT_ARRIVAL"
  | "INBOUND_CHUTE"
  | "ON_CAROUSEL"
  | "CLAIMED_FROM_BELT";

/** Aktarma bavulunun yolcuya gösterilen adımları (W10a). */
export type TransferStep =
  | "CHECKED_IN"
  | "SECURITY_SCREENED"
  | "LOADED_ON_AIRCRAFT"
  | "UNLOADED_AT_ARRIVAL"
  | "TRANSFER_SORTED"
  | "ONWARD_LOADED";

type Text = { title: string; body: string };

export type Messages = {
  locale: string;
  pageTitle: string;
  nav: { passenger: string; ops: string };
  footer: string;
  language: { label: string };
  passenger: {
    title: string;
    intro: string;
    pnr: string;
    surname: string;
    submit: string;
    searching: string;
    empty: string;
    resultsLabel: string;
    missingInput: string;
    notFound: string;
    tooMany: string;
    network: string;
    updated: (time: string, live: boolean) => string;
  };
  card: { tag: string; flight: string; carousel: string };
  steps: Record<StepState, { short: string; message: string }>;
  stepperLabel: string;
  state: {
    onCarousel: (carousel: string) => string;
    offloaded: string;
    exitReview: string;
    updating: string;
  };
  notices: Record<BagNotice, Text>;
  transfer: {
    steps: Record<TransferStep, { short: string; message: string }>;
    onward: string;
    note: string;
    onwardOffloaded: string;
    collectAt: (airport: string) => string;
  };
  eta: {
    title: string;
    likely: string;
    caveat: string;
    range: (early: string, late: string, mid: string) => string;
    basis: Record<EtaView["basis"], string>;
  };
  minutes: { underOne: string; min: (m: number) => string; hours: (h: number, m: number) => string };
};

const tr: Messages = {
  locale: "tr-TR",
  pageTitle: "AeroBag bagaj takibi",
  nav: { passenger: "Bagajım nerede", ops: "Operasyon panosu" },
  footer: "Simülasyon verisiyle çalışan bir mühendislik projesidir; gerçek bir havalimanı veya havayolu verisi içermez.",
  language: { label: "Dil" },
  passenger: {
    title: "Bagajım ne zaman gelir?",
    intro: "Rezervasyon kodunuzu ve soyadınızı girin; bagajınızın nerede olduğunu ve banda ne zaman çıkacağını görün.",
    pnr: "Rezervasyon kodu (PNR)",
    surname: "Soyad",
    submit: "Bagajımı bul",
    searching: "Bagajınız aranıyor…",
    empty: "Sonuçlar burada görünecek.",
    resultsLabel: "Bagaj durumu",
    missingInput: "Rezervasyon kodunu ve soyadı girin.",
    notFound: "Bu rezervasyon kodu ve soyad ile eşleşen bir bagaj bulamadık. Bilgileri kontrol edip tekrar deneyin.",
    tooMany: "Çok fazla deneme yaptınız. Bir dakika bekleyip tekrar deneyin.",
    network: "Sunucuya ulaşamadık. Bağlantınızı kontrol edip tekrar deneyin.",
    updated: (time, live) =>
      `Son güncelleme ${time}. ${live ? "Canlı bağlantı açık, değişiklikler anında gelir." : "Sayfa kendiliğinden yenilenir."}`,
  },
  card: { tag: "Bagaj etiketi", flight: "Uçuş", carousel: "Bant" },
  steps: {
    CHECKED_IN: { short: "Teslim edildi", message: "Bagajınız teslim alındı." },
    SECURITY_SCREENED: { short: "Güvenlik", message: "Bagajınız güvenlik kontrolünden geçti." },
    LOADED_ON_AIRCRAFT: { short: "Uçakta", message: "Bagajınız uçağa yüklendi." },
    UNLOADED_AT_ARRIVAL: { short: "İndirildi", message: "Bagajınız uçaktan indirildi." },
    INBOUND_CHUTE: { short: "Şutta", message: "Bagajınız banda verilmek üzere." },
    ON_CAROUSEL: { short: "Bantta", message: "Bagajınız bantta, alabilirsiniz." },
    CLAIMED_FROM_BELT: { short: "Alındı", message: "Bagajınız banttan alındı." },
  },
  stepperLabel: "Bagaj yolculuğu",
  state: {
    onCarousel: (c) => `Bagajınız ${c} numaralı bantta, alabilirsiniz.`,
    offloaded: "Bagajınız uçağa alınmadı. Havayolu ile iletişime geçin.",
    // ARCHITECTURE.md 6.3: sahibine bilgi verilir; müdahaleye teşvik eden alarm dili kullanılmaz.
    exitReview: "Bir bagajınızla ilgili beklenmedik bir çıkış kaydı algılandı. Lütfen bagaj hizmetleriyle iletişime geçin.",
    updating: "Bagajınızın durumu güncelleniyor.",
  },
  // W4: bir uyarı kesin hüküm değildir (okuyucu kaçırması da aynı izi bırakır); metin alarm dili kullanmaz ve ne yapılacağını söyler.
  notices: {
    NOT_LOADED: {
      title: "Bavulunuzun bu uçağa yüklendiğine dair kayıt yok",
      body: "Uçaktan indirme tamamlandı ve bavulunuz okunmadı. Bant başında beklemek yerine bagaj hizmetlerine başvurun. Bavulunuz okunursa bu sayfa güncellenir.",
    },
    MISSING_AT_ARRIVAL: {
      title: "Uçaktan indirme tamamlandı, bavulunuz okunmadı",
      body: "Bant başında beklemek yerine bagaj hizmetlerine başvurun. Bavulunuz okunursa bu sayfa güncellenir.",
    },
    ARRIVAL_DELAYED: {
      title: "Bavul indirme beklenenden geç başladı",
      body: "Uçak park yerinde, bavullar henüz indirilmedi. İndirme başlayınca bu sayfa güncellenir.",
    },
    LIKELY_ON_BELT: {
      title: "Bavulunuz büyük olasılıkla bantta",
      body: "Bu havalimanında bant başında okuma yapılmıyor; tahmini süre geçti. Bavulunuzu bantta göremezseniz bagaj hizmetlerine başvurun.",
    },
  },
  transfer: {
    steps: {
      CHECKED_IN: { short: "Teslim edildi", message: "Bagajınız teslim alındı." },
      SECURITY_SCREENED: { short: "Güvenlik", message: "Bagajınız güvenlik kontrolünden geçti." },
      LOADED_ON_AIRCRAFT: { short: "Uçakta", message: "Bagajınız ilk uçağınıza yüklendi." },
      UNLOADED_AT_ARRIVAL: { short: "İndirildi", message: "Bagajınız aktarma havalimanında uçaktan indirildi." },
      TRANSFER_SORTED: { short: "Aktarmada", message: "Bagajınız aktarma hattından geçti, bağlantı uçağınıza yüklenecek." },
      ONWARD_LOADED: { short: "Bağlantı uçağında", message: "Bagajınız bağlantı uçağınıza yüklendi." },
    },
    onward: "Bağlantı",
    note: "Bu bagaj bu havalimanında banda gelmez; bağlantı uçuşunuza aktarılır.",
    onwardOffloaded: "Bagajınız bağlantı uçağından indirildi. Havayolu ile iletişime geçin.",
    collectAt: (a) => `Bagajınızı varış havalimanında (${a}) bant başından alacaksınız.`,
  },
  eta: {
    title: "Bantta görünme tahmini",
    likely: "En olası süre",
    caveat: "Aralığın dışına çıkma ihtimali düşüktür ama sıfır değildir.",
    range: (early, late, mid) => `Tahmin aralığı ${early} ile ${late} arası, en olası ${mid}`,
    basis: {
      ML: "Bu tahmin, uçuşun özelliklerine (gövde tipi, hava, mesafe, bavul sayısı) bakan ve son bavulların gerçek sürelerine göre kendini güncelleyen bir makine öğrenmesi modelinden geliyor.",
      ML_FLIGHT:
        "Bu tahmin, uçuşun özelliklerine bakan bir makine öğrenmesi modelinden geliyor. Uçuştaki ilk bavuldan sonuncuya kadar olan aralığı kapsar; bavulunuzun sırası bilinmediği için geniştir.",
      PHYSICS: "Bu tahmin, şuttan banta olan mesafe ve bant hızından hesaplandı; yaklaşık 15 saniyelik bir pay içerir.",
      HISTORY: "Bu tahmin, simülasyondaki geçmiş uçuşların gerçekleşen sürelerinden hesaplandı.",
      FALLBACK: "Yeterli geçmiş veri yok; tahmin varsayılan bir aralığa dayanıyor ve geniş olabilir.",
    },
  },
  minutes: { underOne: "1 dakikadan az", min: (m) => `${m} dk`, hours: (h, m) => `${h} sa ${m} dk` },
};

const en: Messages = {
  locale: "en-GB",
  pageTitle: "AeroBag baggage tracking",
  nav: { passenger: "Where's my bag", ops: "Operations" },
  footer: "An engineering project running on simulated data; it contains no real airport or airline data.",
  language: { label: "Language" },
  passenger: {
    title: "When will my bag arrive?",
    intro: "Enter your booking reference and surname to see where your bag is and when it will reach the belt.",
    pnr: "Booking reference (PNR)",
    surname: "Surname",
    submit: "Find my bag",
    searching: "Looking for your bag…",
    empty: "Your results will appear here.",
    resultsLabel: "Bag status",
    missingInput: "Enter your booking reference and surname.",
    notFound: "We couldn't find a bag matching this booking reference and surname. Check the details and try again.",
    tooMany: "Too many attempts. Wait a minute and try again.",
    network: "We couldn't reach the server. Check your connection and try again.",
    updated: (time, live) =>
      `Last updated ${time}. ${live ? "Live connection on; changes appear instantly." : "This page refreshes automatically."}`,
  },
  card: { tag: "Bag tag", flight: "Flight", carousel: "Belt" },
  steps: {
    CHECKED_IN: { short: "Checked in", message: "Your bag has been checked in." },
    SECURITY_SCREENED: { short: "Security", message: "Your bag has passed security screening." },
    LOADED_ON_AIRCRAFT: { short: "On aircraft", message: "Your bag has been loaded onto the aircraft." },
    UNLOADED_AT_ARRIVAL: { short: "Unloaded", message: "Your bag has been unloaded from the aircraft." },
    INBOUND_CHUTE: { short: "To belt", message: "Your bag is about to go onto the belt." },
    ON_CAROUSEL: { short: "On belt", message: "Your bag is on the belt, ready to collect." },
    CLAIMED_FROM_BELT: { short: "Collected", message: "Your bag has been collected from the belt." },
  },
  stepperLabel: "Bag journey",
  state: {
    onCarousel: (c) => `Your bag is on belt ${c}, ready to collect.`,
    offloaded: "Your bag was not loaded onto the aircraft. Please contact your airline.",
    exitReview: "An unexpected exit record was detected for one of your bags. Please contact baggage services.",
    updating: "Your bag's status is being updated.",
  },
  notices: {
    NOT_LOADED: {
      title: "There is no record of your bag being loaded onto this aircraft",
      body: "Unloading is complete and your bag has not been scanned. Instead of waiting at the belt, please go to baggage services. If your bag is scanned, this page will update.",
    },
    MISSING_AT_ARRIVAL: {
      title: "Unloading is complete, but your bag has not been scanned",
      body: "Instead of waiting at the belt, please go to baggage services. If your bag is scanned, this page will update.",
    },
    ARRIVAL_DELAYED: {
      title: "Unloading started later than expected",
      body: "The aircraft is at the stand, but the bags have not been unloaded yet. This page will update when unloading starts.",
    },
    LIKELY_ON_BELT: {
      title: "Your bag is most likely on the belt",
      body: "Bags are not scanned at the belt at this airport, and the estimated time has passed. If you can't see your bag on the belt, please go to baggage services.",
    },
  },
  transfer: {
    steps: {
      CHECKED_IN: { short: "Checked in", message: "Your bag has been checked in." },
      SECURITY_SCREENED: { short: "Security", message: "Your bag has passed security screening." },
      LOADED_ON_AIRCRAFT: { short: "On aircraft", message: "Your bag has been loaded onto your first flight." },
      UNLOADED_AT_ARRIVAL: { short: "Unloaded", message: "Your bag has been unloaded at the connecting airport." },
      TRANSFER_SORTED: { short: "In transfer", message: "Your bag has passed transfer sorting and will be loaded onto your connecting flight." },
      ONWARD_LOADED: { short: "On connection", message: "Your bag has been loaded onto your connecting flight." },
    },
    onward: "Connecting",
    note: "This bag will not come out on a belt here; it is transferred to your connecting flight.",
    onwardOffloaded: "Your bag was taken off your connecting flight. Please contact your airline.",
    collectAt: (a) => `You will collect your bag from the belt at your destination airport (${a}).`,
  },
  eta: {
    title: "Estimated time on the belt",
    likely: "Most likely",
    caveat: "It could fall outside this range; that is unlikely, but possible.",
    range: (early, late, mid) => `Estimate between ${early} and ${late}, most likely ${mid}`,
    basis: {
      ML: "This estimate comes from a machine-learning model that looks at the flight's characteristics (aircraft type, weather, distance, number of bags) and updates itself using how long recent bags actually took.",
      ML_FLIGHT:
        "This estimate comes from a machine-learning model that looks at the flight's characteristics. It spans the flight's first bag to its last, so it is wide: your bag's place in the order is not known.",
      PHYSICS: "This estimate is calculated from the distance between the chute and the belt and the belt speed; it includes a margin of about 15 seconds.",
      HISTORY: "This estimate is based on how long bags actually took on past flights in the simulation.",
      FALLBACK: "There is not enough past data, so the estimate uses a default range and may be wide.",
    },
  },
  minutes: { underOne: "under 1 min", min: (m) => `${m} min`, hours: (h, m) => `${h} h ${m} min` },
};

export const MESSAGES: Record<Lang, Messages> = { tr, en };

const STORAGE_KEY = "aerobag.lang";

function isLang(value: unknown): value is Lang {
  return value === "tr" || value === "en";
}

/** Tarayıcının ilk dili Türkçe ise Türkçe, değilse İngilizce (yabancı yolcu için ortak dil). */
export function browserLang(languages: readonly string[]): Lang {
  return (languages[0] ?? "").toLowerCase().startsWith("tr") ? "tr" : "en";
}

/** Seçim sırası: kullanıcının daha önce seçtiği dil, yoksa tarayıcı dili. */
function initialLang(): Lang {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (isLang(saved)) return saved;
  } catch {
    // Depolama kapalıysa tarayıcı diline bakılır.
  }
  const langs = navigator.languages?.length ? navigator.languages : [navigator.language];
  return browserLang(langs);
}

type LangContext = { lang: Lang; setLang: (lang: Lang) => void; m: Messages };

const Ctx = createContext<LangContext | null>(null);

export function LangProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(initialLang);

  const setLang = useCallback((next: Lang) => {
    setLangState(next);
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // Seçim yalnızca bu oturumda geçerli olur.
    }
  }, []);

  useEffect(() => {
    document.documentElement.lang = lang;
    document.title = MESSAGES[lang].pageTitle;
  }, [lang]);

  const value = useMemo(() => ({ lang, setLang, m: MESSAGES[lang] }), [lang, setLang]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useI18n(): LangContext {
  const value = useContext(Ctx);
  if (!value) throw new Error("useI18n LangProvider içinde kullanılmalı.");
  return value;
}
