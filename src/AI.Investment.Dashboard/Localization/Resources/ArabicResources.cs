namespace AI.Investment.Dashboard.Localization.Resources;

/// <summary>Every user-facing string, in Arabic.</summary>
/// <remarks>
/// The key set is identical to <see cref="EnglishResources"/> by construction, and a test proves
/// it. Arabic is a first-class UI here: the document direction, the layout mirroring and the
/// formatting of dates and numbers all follow from choosing it, rather than English strings being
/// swapped inside an English layout.
/// </remarks>
public static class ArabicResources
{
    public static IReadOnlyDictionary<string, string> Values { get; } = new Dictionary<string, string>(
        StringComparer.Ordinal)
    {
        // ---- product and shell -----------------------------------------------------------
        ["app.name"] = "محلل الاستثمار الذكي",
        ["app.subtitle"] = "منصة تحليل الاستثمار",
        ["nav.overview"] = "نظرة عامة",
        ["nav.market"] = "بيانات السوق",
        ["nav.opportunities"] = "الفرص",
        ["nav.portfolio"] = "المحفظة",
        ["nav.capital"] = "رأس المال",
        ["nav.risk"] = "المخاطر",
        ["nav.validation"] = "التحقق",
        ["nav.operations"] = "التشغيل",
        ["nav.safety"] = "السلامة والاستقلالية",
        ["nav.menu"] = "القائمة الرئيسية",
        ["nav.open"] = "فتح القائمة",
        ["nav.close"] = "إغلاق القائمة",
        ["shell.language"] = "اللغة",
        ["shell.signedInAs"] = "تم تسجيل الدخول باسم",
        ["shell.signOut"] = "تسجيل الخروج",
        ["shell.refresh"] = "تحديث",
        ["shell.refreshing"] = "جارٍ التحديث…",
        ["shell.lastRefreshed"] = "آخر تحديث {0}",
        ["shell.neverRefreshed"] = "لم يتم التحميل بعد",
        ["shell.systemStatus"] = "حالة النظام",

        // ---- sign in ---------------------------------------------------------------------
        ["signIn.title"] = "تسجيل الدخول",
        ["signIn.intro"] =
            "هذه المنصة للقراءة في الأغلب، وكل إجراء يُسجَّل باسم المشغّل الذي طلبه. أدخل مفتاح " +
            "المشغّل الصادر لك.",
        ["signIn.keyLabel"] = "مفتاح المشغّل",
        ["signIn.keyHint"] = "يُحفظ المفتاح لجلسة المتصفح هذه فقط ولا يُعرض مرة أخرى.",
        ["signIn.submit"] = "تسجيل الدخول",
        ["signIn.checking"] = "جارٍ التحقق…",
        ["signIn.rejected"] = "لم يتم التعرّف على هذا المفتاح.",
        ["signIn.empty"] = "أدخل مفتاح المشغّل.",
        ["signIn.unreachable"] = "تعذّر الوصول إلى المنصة.",
        ["signIn.noPrivileges"] =
            "تم تسجيل دخولك، لكن لا توجد صلاحيات ممنوحة لهذا المفتاح. اطلب من المسؤول منحك " +
            "الصلاحيات التي تحتاجها.",

        // ---- generic states --------------------------------------------------------------
        ["state.loading"] = "جارٍ التحميل…",
        ["state.empty"] = "لا يوجد شيء لعرضه بعد.",
        ["state.unavailable"] = "غير متاح",
        ["state.unknown"] = "غير معروف",
        ["state.notMeasured"] = "لم يُقَس",
        ["state.retry"] = "أعد المحاولة",
        ["state.noData"] = "لا توجد بيانات",
        ["state.notApplicable"] = "—",

        // ---- errors ----------------------------------------------------------------------
        ["error.title"] = "حدث خطأ ما",
        ["error.unauthorized"] = "لم تعد جلستك صالحة. سجّل الدخول مرة أخرى.",
        ["error.forbidden"] = "أنت مسجّل الدخول، لكن هذا المفتاح لا يملك الصلاحية التي تحتاجها هذه الصفحة.",
        ["error.notFound"] = "هذا غير متاح على هذه المنصة.",
        ["error.rateLimited"] = "طلبات كثيرة جدًا. انتظر لحظة ثم أعد المحاولة.",
        ["error.server"] = "أبلغت المنصة عن فشل.",
        ["error.network"] = "تعذّر الوصول إلى المنصة. تأكد من أنها تعمل.",
        ["error.validation"] = "رُفض الطلب لعدم صلاحيته.",

        // ---- overview --------------------------------------------------------------------
        ["overview.title"] = "نظرة عامة",
        ["overview.health"] = "الحالة الصحية",
        ["overview.portfolioValue"] = "قيمة المحفظة",
        ["overview.cash"] = "النقد",
        ["overview.openPositions"] = "المراكز المفتوحة",
        ["overview.unrealised"] = "أرباح وخسائر غير محققة",
        ["overview.realised"] = "أرباح وخسائر محققة",
        ["overview.opportunities"] = "الفرص",
        ["overview.escalations"] = "التصعيدات المفتوحة",
        ["overview.autonomy"] = "مستوى الاستقلالية",
        ["overview.liveExecution"] = "التنفيذ الحيّ",
        ["overview.observationWindow"] = "نافذة المراقبة",
        ["overview.windowInactive"] = "لم تبدأ — لا توجد ملاحظات سوقية مسجّلة",
        ["overview.windowActive"] = "قيد التجميع",

        // ---- portfolio -------------------------------------------------------------------
        ["portfolio.title"] = "المحفظة",
        ["portfolio.instrument"] = "الأداة المالية",
        ["portfolio.quantity"] = "الكمية",
        ["portfolio.averageCost"] = "متوسط التكلفة",
        ["portfolio.costBasis"] = "أساس التكلفة",
        ["portfolio.currentPrice"] = "السعر الحالي",
        ["portfolio.marketValue"] = "القيمة السوقية",
        ["portfolio.exposure"] = "الانكشاف",
        ["portfolio.valuation"] = "التقييم",
        ["portfolio.totalValue"] = "القيمة الإجمالية",
        ["portfolio.totalUnavailable"] = "تعذّر تحديد الإجمالي",
        ["portfolio.totalUnavailableWhy"] =
            "{0} من {1} مراكز مفتوحة بلا سعر منشور، فالإجمالي سيكون أقل من الحقيقة بينما يبدو " +
            "كأنه إجابة.",
        ["portfolio.empty"] = "لم تُسجَّل أي مراكز.",
        ["portfolio.positionDetail"] = "تفاصيل المركز",
        ["portfolio.back"] = "العودة إلى المحفظة",

        // ---- valuation states (backend enum → label) --------------------------------------
        ["valuation.Available"] = "مُقيَّم",
        ["valuation.NoObservedPrice"] = "لا يوجد سعر مرصود",
        ["valuation.NotHeld"] = "مغلق",
        ["valuation.Unknown"] = "غير معروف",

        // ---- market ----------------------------------------------------------------------
        ["market.title"] = "بيانات السوق",
        ["market.sources"] = "المصادر",
        ["market.source"] = "المصدر",
        ["market.active"] = "مُفعَّل",
        ["market.inactive"] = "غير مُفعَّل",
        ["market.freshness"] = "حداثة البيانات",
        ["market.marketDate"] = "تاريخ السوق",
        ["market.publishedAt"] = "وقت النشر لدى المصدر",
        ["market.retrievedAt"] = "وقت الاستقبال لدى المنصة",
        ["market.timestampNote"] =
            "تاريخ السوق ووقت النشر يخصّان المصدر. وقت الاستقبال يخصّنا، ولا يُستخدم أبدًا كوقت نشر.",
        ["market.runs"] = "عمليات الاستقبال",
        ["market.noObservations"] = "لم تُسجَّل أي ملاحظات سوقية على هذه المنصة.",

        // ---- opportunities ---------------------------------------------------------------
        ["opportunities.title"] = "الفرص",
        ["opportunities.status"] = "الحالة",
        ["opportunities.score"] = "الدرجة",
        ["opportunities.risk"] = "المخاطر",
        ["opportunities.created"] = "تاريخ الإنشاء",
        ["opportunities.evidence"] = "الأدلة",
        ["opportunities.evidenceCount"] = "{0} ملاحظات مستشهد بها",
        ["opportunities.detail"] = "تفاصيل الفرصة",
        ["opportunities.empty"] = "لم يتم اكتشاف أي فرص.",
        ["opportunities.emptyWhy"] = "يعمل الاكتشاف على الأسعار المرصودة، ولم يُسجَّل أي منها بعد.",

        // ---- capital ---------------------------------------------------------------------
        ["capital.title"] = "رأس المال",
        ["capital.ledger"] = "دفتر الأستاذ",
        ["capital.account"] = "الحساب",
        ["capital.balance"] = "الرصيد",
        ["capital.entries"] = "القيود",
        ["capital.debit"] = "مدين",
        ["capital.credit"] = "دائن",
        ["capital.amount"] = "المبلغ",
        ["capital.occurredAt"] = "وقت الحدوث",
        ["capital.description"] = "الوصف",
        ["capital.ledgerNote"] =
            "كل رقم هنا مشتق من قيود مزدوجة، لا من رصيد مخزَّن، ولا شيء منه تقييم سوقي.",
        ["capital.empty"] = "لا يحتوي دفتر الأستاذ على أي قيود.",

        // ---- risk ------------------------------------------------------------------------
        ["risk.title"] = "المخاطر",
        ["risk.limits"] = "الحدود",
        ["risk.limit"] = "الحد",
        ["risk.ceiling"] = "السقف",
        ["risk.exposureByInstrument"] = "الانكشاف حسب الأداة",
        ["risk.killSwitch"] = "مفتاح الإيقاف",
        ["risk.killSwitchEngaged"] = "مُفعَّل — كل إجراء مرفوض",
        ["risk.killSwitchDisengaged"] = "غير مُفعَّل",
        ["risk.killSwitchUnknown"] = "غير معروف — يُعامَل كمُفعَّل",
        ["risk.exposureNote"] = "يُقاس الانكشاف بالتكلفة، على نفس أساس دفتر الأستاذ.",
        ["risk.empty"] = "لم يُسجَّل أي انكشاف.",

        // ---- validation ------------------------------------------------------------------
        ["validation.title"] = "التحقق",
        ["validation.notEstablished"] = "لم تُثبَت أي نتيجة",
        ["validation.notEstablishedWhy"] =
            "لم تنجُ أي تنبؤات مقبولة من حارس النقطة الزمنية، لأنه لم تُسجَّل ملاحظات سوقية. هذا " +
            "ليس قياسًا فاشلًا، بل قياسًا لم يحدث.",
        ["validation.hitRate"] = "نسبة الإصابة",
        ["validation.calibration"] = "المعايرة",
        ["validation.benchmark"] = "المؤشر المرجعي",
        ["validation.sampleSize"] = "التنبؤات المقبولة",

        // ---- operations ------------------------------------------------------------------
        ["operations.title"] = "التشغيل",
        ["operations.cycles"] = "دورات التشغيل",
        ["operations.escalations"] = "التصعيدات",
        ["operations.shadow"] = "قرارات الظل",
        ["operations.shadowNote"] =
            "قرارات الظل قياسات لما كان سيحدث. لم يُنفَّذ أي شيء منها.",
        ["operations.raisedAt"] = "وقت الرفع",
        ["operations.reason"] = "السبب",
        ["operations.capability"] = "القدرة",
        ["operations.acknowledged"] = "تم الاطلاع",
        ["operations.resolved"] = "تمت المعالجة",
        ["operations.open"] = "مفتوح",

        // ---- safety ----------------------------------------------------------------------
        ["safety.title"] = "السلامة والاستقلالية",
        ["safety.currentLevel"] = "المستوى الحالي",
        ["safety.liveExecution"] = "التنفيذ الحيّ",
        ["safety.liveExecutionUnavailable"] = "غير متاح",
        ["safety.liveExecutionNote"] =
            "لا يمكن لأي منفذ تنفيذ في هذه المنصة الوصول إلى سوق حقيقي. هذا أمر بنيوي، لا إعداد.",
        ["safety.promotion"] = "الترقية",
        ["safety.promotionState"] = "حالة الترقية",
        ["safety.grants"] = "منح القدرات",
        ["safety.unmetCriteria"] = "المعايير غير المستوفاة",
        ["safety.noWarrant"] = "لا يوجد أمر ترقية.",
        ["safety.l3"] = "L3 — التحضير للموافقة",
        ["safety.l3Note"] = "تُعدّ المنصة المقترحات ليوافق عليها شخص. وهي لا تتصرف من تلقاء نفسها.",
    };
}
