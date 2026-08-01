# Centerix — التوثيق الكامل لقاعدة البيانات (ERD v3)

**عدد الجداول:** 84  ·  **عدد العلاقات الموثقة:** 103  ·  **النموذج:** Multi-Tenant Hybrid (Shared Database + Database-per-Tenant)

---

## 1) نظرة معمارية عامة

### نموذج العزل (Isolation Model)
- **لا يوجد فصل Schema.** كل الجداول (Platform وTenant) تعيش فعليًا في نفس بنية الجداول سواء كان المركز على قاعدة بيانات مشتركة أو مستقلة.
- **Shared Database:** عدة Tenants على نفس الجداول، معزولين فقط بعمود `TenantId` الموجود في **كل جدول Tenant-scoped بدون استثناء**.
- **Dedicated Database:** Tenant له قاعدة بيانات منفصلة بالكامل، لكن بنفس الـ Schema بالضبط (بما فيها عمود `TenantId` بقيمة ثابتة واحدة) — لسهولة النقل بين النموذجين بدون تغيير الكود.
- التوجيه بين الحالتين يتم عبر `Tenants.IsolationMode` + `Tenants.ConnectionStringRef` (مرجع لـ Key Vault، أبدًا connection string خام).
- أي Foreign Key بين جدولين Tenant-scoped (مثل `Students.BranchId → Branches.BranchId`) هو FK حقيقي مفروض على مستوى قاعدة البيانات **فقط** لو المركز على Shared DB. لو Dedicated، يبقى نفس القيد لكنه داخل قاعدة بيانات المركز نفسها فقط (سليم بنفس الطريقة). العلاقة بين أي جدول Tenant-scoped وجدول `Tenants` المركزي (عبر `TenantId`) هي علاقة **منطقية/Application-level** فقط عند Dedicated DB، لأن FK فعلي عبر سيرفرات مختلفة غير ممكن تقنيًا.

### تطبيق حدود الخطة (Plan Enforcement)
- **السياسة: Hard Block.** أي محاولة لتجاوز حد الخطة (عدد الطلاب/المستخدمين/الفروع/المعلمين) تُرفض في وقت الإدخال.
- الفحص اللحظي يتم **داخل قاعدة بيانات الـ Tenant نفسها** (COUNT حي وقت الكتابة) — دقيق دائمًا بغض النظر عن نوع العزل.
- الحد الفعلي `EffectiveMaxX` = حد الخطة الأساسية (`Plans.MaxX`) + مجموع الإضافات النشطة (`TenantAddOns`).
- جدول `TenantUsageCounters` المركزي هو **تقرير دوري (async)** يُحدَّث من كل قاعدة بيانات (خصوصًا الـ Dedicated منها عبر Background Job)، ويُستخدم فقط لعرض لوحة تحكم الإدارة والتنبيهات الاستباقية — وليس مصدر الفحص اللحظي.

### الفوترة والإضافات (Billing & Add-ons)
- نظام فوترة كامل (`Invoices` + `InvoiceLines`) بدل سجل دفع بسيط، لدعم بنود متعددة (اشتراك أساسي + إضافات + ترقيات) في نفس الفاتورة.
- كل سعر (خطة أو إضافة) يُجمَّد وقت الشراء (`SnapshotPrice` / `SnapshotUnitPrice`) فلا يتأثر بتغيير الأسعار العامة لاحقًا.
- الإضافات (Add-ons) مثل فرع إضافي أو بلوك طلاب لها تسعير متدرج حسب الكمية (`AddOnPricingTiers`) وتُحسب بتناسب الأيام المتبقية من الدورة (Proration) عند الشراء منتصف الشهر.
- **سياسة الإلغاء:** لا استرداد جزئي — أي إضافة مُلغاة تبقى سارية (محسوبة ضمن الحد) لحد نهاية الدورة المدفوعة فقط، ولا تتجدد في الفاتورة التالية.

### الإدارة المركزية (Platform Admin)
- موظفو المنصة (`PlatformUsers`) في جدول **منفصل تمامًا** عن `Users` الخاص بمستخدمي كل مركز — لتفادي خلط الصلاحيات وتقليل نطاق أي اختراق أمني محتمل.
- أي دخول لموظف دعم بالنيابة عن مستخدم مركز مُوثَّق إلزاميًا في `ImpersonationLogs`.
- الإدارة تدير كل المشتركين (سواء Shared أو Dedicated) من نفس قاعدة البيانات المركزية عبر جداول `Tenants` و`TenantUsageCounters` و`Invoices`، بدون الحاجة للاتصال المباشر بكل قاعدة بيانات مستقلة وقت العرض.

### نظام الإحالة (Referral) على مستويين
- **مركز → مركز** (`TenantReferrals` + `TenantReferralCodes`): مكافأة (خصم أو مدة إضافية) عند جلب مركز مشترك جديد.
- **طالب → طالب** (`StudentReferrals` + `ReferralCodes`): مكافأة داخل كل مركز عند جلب طالب جديد.
- في الحالتين، المكافأة **لا تُفعَّل** إلا بعد حالة `Qualified` (تأكيد حقيقي مثل أول فاتورة/قسط مدفوع فعليًا) لمنع الاستغلال، وتُطبَّق عبر نظام محفظة أرصدة موحّد (`TenantCredits` / `StudentCredits`).

### معايير عامة على كل الجداول (Standards)
- **RowVersion** على أي جدول قابل للتعديل المتزامن (Students, Payments, StudentFees, Groups...) لحل تعارضات التزامن.
- **أعمدة Audit موحّدة** (`CreatedAt/CreatedBy/ModifiedAt/ModifiedBy/DeletedAt/DeletedBy`) على الجداول الجوهرية القابلة للتعديل، وليس فقط على `Students`.
- **Soft Delete** (`DeletedAt`) بدل الحذف الفعلي في أي جدول بيانات جوهرية.
- **Composite Uniqueness ضمنية:** أي `UK` على جدول Tenant-scoped (مثل `Users.Email` أو `Students.QRCode`) هي فريدة **ضمن نطاق `TenantId` نفسه**، مش عالميًا.

---

## 2) فهرس الجداول حسب الدومين

- **🏢 المنصة — النواة والخطط (Platform Core & Plans)** (4): `Tenants`, `Plans`, `Features`, `PlanFeatures`
- **💳 المنصة — الاشتراكات والإضافات (Subscriptions & Add-ons)** (6): `TenantPlans`, `AddOnCatalog`, `AddOnPricingTiers`, `TenantAddOns`, `TenantUsageCounters`, `TenantLimitOverrides`
- **🧾 المنصة — الفوترة (Billing & Invoicing)** (4): `Invoices`, `InvoiceLines`, `PlatformPayments`, `TenantCredits`
- **📈 المنصة — CRM والإحالات والعمليات التشغيلية** (7): `TenantReferralCodes`, `TenantReferrals`, `TenantCRMLeads`, `TenantSettings`, `TenantProvisioningJobs`, `TenantSchemaVersion`, `PlatformAuditLog`
- **🔐 المنصة — موظفو الإدارة الداخليين (Platform Staff)** (6): `PlatformUsers`, `PlatformRoles`, `PlatformPermissions`, `PlatformUserRoles`, `PlatformRolePermissions`, `ImpersonationLogs`
- **🔑 Tenant — M-12 الأمان والصلاحيات** (8): `Users`, `Roles`, `Permissions`, `UserRoles`, `RolePermissions`, `RefreshTokens`, `AuditLog`, `LoginHistory`
- **🎓 Tenant — M-01 الطلاب** (5): `Branches`, `AcademicStages`, `AcademicYears`, `Students`, `AttendanceLogs`
- **👨‍🏫 Tenant — M-02 المعلمون** (5): `Subjects`, `Teachers`, `TeacherSalaryConfig`, `SalaryPayments`, `TeacherRatings`
- **🗓️ Tenant — M-03 الجدولة** (5): `Rooms`, `Groups`, `GroupSchedule`, `StudentGroups`, `Waitlist`
- **💰 Tenant — M-04 المالية** (6): `FeeTypes`, `StudentFees`, `Payments`, `StudentCredits`, `ExpenseCategories`, `Expenses`
- **📝 Tenant — M-05 الأكاديمي (تقييمات وامتحانات)** (4): `Assessments`, `AssessmentResults`, `QuestionBank`, `ExamSessions`
- **🧑‍💼 Tenant — M-06 الموارد البشرية** (3): `Employees`, `LeaveTypes`, `LeaveRequests`
- **🔔 Tenant — M-07 الاتصالات والإشعارات** (5): `NotificationTemplates`, `NotificationLogs`, `Announcements`, `Notifications`, `NotificationRecipients`
- **🚀 Tenant — M-08 النمو والإحالات** (5): `LeadSources`, `CRMLeads`, `ReferralCodes`, `StudentReferrals`, `ChurnScores`
- **📚 Tenant — M-09 نظام التعلم الإلكتروني (LMS)** (6): `Courses`, `Units`, `Lessons`, `Assignments`, `AssignmentSubmissions`, `StudentProgress`
- **👪 Tenant — M-10 أولياء الأمور** (2): `Parents`, `StudentParents`
- **🗂️ Tenant — M-11 التخزين والملفات** (2): `Files`, `EntityFiles`
- **🗒️ Tenant — M-14 الملاحظات** (1): `Notes`

---

## 3) شرح تفصيلي لكل جدول

### 🏢 المنصة — النواة والخطط (Platform Core & Plans)

#### `Tenants`  <sub>(platform · Core)</sub>

**الوظيفة:** الجدول المركزي لكل مركز مشترك في المنصة. نقطة الربط الوحيدة بين عالم الـ Shared DB وعالم الـ Dedicated DB عبر IsolationMode. أي جدول Tenant-scoped بيحمل TenantId بيرجع لهذا الجدول ضمنيًا (FK حقيقي لو Shared، منطقي/Application-level لو Dedicated).

- **المفتاح الأساسي:** TenantId
- **مفاتيح خارجية:** CurrentPlanId
- **مفاتيح فريدة:** Slug, Subdomain
- **جداول ترتبط به:** `TenantPlans`, `TenantAddOns`, `TenantUsageCounters`, `TenantLimitOverrides`, `Invoices`, `TenantCredits`, `TenantReferralCodes`, `TenantReferrals`, `TenantSettings`, `TenantProvisioningJobs`, `TenantSchemaVersion`, `PlatformAuditLog`, `ImpersonationLogs`

#### `Plans`  <sub>(platform · Billing)</sub>

**الوظيفة:** كتالوج خطط الاشتراك القياسية (Starter/Pro/Enterprise). يحدد الحدود الافتراضية (طلاب/مستخدمين/فروع/معلمين) قبل إضافة أي Add-ons.

- **المفتاح الأساسي:** PlanId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `TenantPlans`, `PlanFeatures`

#### `Features`  <sub>(platform · Flags)</sub>

**الوظيفة:** كتالوج الميزات القابلة للتفعيل/الإيقاف (Feature Flags) عبر النظام، مثل LMS أو Growth Analytics.

- **المفتاح الأساسي:** FeatureId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `PlanFeatures`

#### `PlanFeatures`  <sub>(platform · Junction)</sub>

**الوظيفة:** جدول ربط: أي ميزات مفعّلة ضمن أي خطة اشتراك.

- **المفتاح الأساسي:** PlanFeatureId
- **مفاتيح خارجية:** PlanId, FeatureId
- **يرتبط بـ:** `Plans`, `Features`

---

### 💳 المنصة — الاشتراكات والإضافات (Subscriptions & Add-ons)

#### `TenantPlans`  <sub>(platform · Subscription)</sub>

**الوظيفة:** سجل تاريخي لاشتراكات كل Tenant. SnapshotPrice يجمّد السعر وقت الاشتراك فلا يتأثر بتغيير سعر الخطة لاحقًا.

- **المفتاح الأساسي:** TenantPlanId
- **مفاتيح خارجية:** TenantId, PlanId
- **يرتبط بـ:** `Tenants`, `Plans`

#### `AddOnCatalog`  <sub>(platform · Catalog)</sub>

**الوظيفة:** كتالوج أنواع الإضافات القابلة للشراء فوق الخطة (فرع إضافي/بلوك طلاب/مستخدم إضافي/SMS إضافي)، قابل للتوسعة من لوحة الإدارة بدون نشر كود جديد.

- **المفتاح الأساسي:** AddOnCatalogId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `AddOnPricingTiers`, `TenantAddOns`

#### `AddOnPricingTiers`  <sub>(platform · Pricing)</sub>

**الوظيفة:** شرائح تسعير متدرجة لكل نوع إضافة (خصم كمية) — مثلاً أول فرعين بسعر، من 3-5 بسعر أقل، وهكذا.

- **المفتاح الأساسي:** TierId
- **مفاتيح خارجية:** AddOnCatalogId
- **يرتبط بـ:** `AddOnCatalog`

#### `TenantAddOns`  <sub>(platform · Purchased)</sub>

**الوظيفة:** الإضافات الفعلية المُشتراة لكل Tenant. عند الإلغاء EffectiveTo تُضبط على نهاية الدورة المدفوعة (بدون استرداد جزئي)، فتبقى الإضافة سارية ضمن الحدود لحد آخر يوم مدفوع.

- **المفتاح الأساسي:** TenantAddOnId
- **مفاتيح خارجية:** TenantId, AddOnCatalogId, InvoiceLineId
- **يرتبط بـ:** `Tenants`, `AddOnCatalog`, `InvoiceLines` (index فقط)

#### `TenantUsageCounters`  <sub>(platform · Metrics)</sub>

**الوظيفة:** عدّاد استخدام مركزي محدَّث دوريًا (Sync Job) لكل Tenant — يُستخدم لعرض الداشبورد الإداري والتنبيهات، وليس للفحص اللحظي وقت الإدخال (ده بيتم داخل الـ Tenant DB مباشرة لـ Hard Block دقيق).

- **المفتاح الأساسي:** TenantId
- **يرتبط بـ:** `Tenants`

#### `TenantLimitOverrides`  <sub>(platform · Custom)</sub>

**الوظيفة:** حدود مخصصة فوق حدود الخطة القياسية لعملاء عندهم اتفاق خاص (Enterprise deal)، بدون تعديل جدول Plans المشترك.

- **المفتاح الأساسي:** OverrideId
- **مفاتيح خارجية:** TenantId, CreatedBy
- **يرتبط بـ:** `Tenants`

---

### 🧾 المنصة — الفوترة (Billing & Invoicing)

#### `Invoices`  <sub>(platform · Billing)</sub>

**الوظيفة:** فاتورة رسمية لكل دورة اشتراك لكل Tenant. رقم متسلسل InvoiceNumber جاهز للتوسع مستقبلاً لمنظومة الفاتورة الإلكترونية.

- **المفتاح الأساسي:** InvoiceId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** InvoiceNumber
- **يرتبط بـ:** `Tenants`
- **جداول ترتبط به:** `InvoiceLines`, `PlatformPayments`

#### `InvoiceLines`  <sub>(platform · Line Items)</sub>

**الوظيفة:** بنود الفاتورة التفصيلية. SourceType يميّز أصل البند (اشتراك أساسي/إضافة/ترقية)، وProratedDays يوثّق حساب التسعير التناسبي عند الشراء منتصف الدورة.

- **المفتاح الأساسي:** LineId
- **مفاتيح خارجية:** InvoiceId
- **يرتبط بـ:** `Invoices`
- **جداول ترتبط به:** `TenantAddOns`, `TenantCredits`

#### `PlatformPayments`  <sub>(platform · Payments)</sub>

**الوظيفة:** دفعات فعلية على مستوى المنصة (تحصيل اشتراك المراكز)، منفصلة عن Payments الخاصة بمصاريف الطلاب داخل كل مركز.

- **المفتاح الأساسي:** PaymentId
- **مفاتيح خارجية:** InvoiceId
- **يرتبط بـ:** `Invoices`

#### `TenantCredits`  <sub>(platform · Wallet)</sub>

**الوظيفة:** محفظة أرصدة/خصومات لكل Tenant (من مكافآت الإحالة، عروض ترويجية، تعويضات) تُطبّق تلقائيًا كخصم على الفاتورة القادمة.

- **المفتاح الأساسي:** CreditId
- **مفاتيح خارجية:** TenantId, AppliedToInvoiceLineId
- **يرتبط بـ:** `Tenants`, `InvoiceLines` (index فقط)

---

### 📈 المنصة — CRM والإحالات والعمليات التشغيلية

#### `TenantReferralCodes`  <sub>(platform · Referral)</sub>

**الوظيفة:** كود إحالة ثابت لكل مركز، يشاركه مع مراكز أخرى محتملة.

- **المفتاح الأساسي:** CodeId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Code
- **يرتبط بـ:** `Tenants`
- **جداول ترتبط به:** `TenantReferrals`

#### `TenantReferrals`  <sub>(platform · Referral)</sub>

**الوظيفة:** سجل إحالة مركز لمركز آخر. المكافأة (خصم/مدة إضافية) لا تُفعَّل إلا بعد Qualified (مثلاً أول فاتورة مدفوعة فعليًا) لمنع الاستغلال.

- **المفتاح الأساسي:** ReferralId
- **مفاتيح خارجية:** ReferrerTenantId, ReferralCodeId
- **مفاتيح فريدة:** ReferredTenantId
- **يرتبط بـ:** `Tenants`, `TenantReferralCodes`

#### `TenantCRMLeads`  <sub>(platform · CRM)</sub>

**الوظيفة:** عملاء محتملون (مراكز جديدة) لم يشتركوا بعد — قِمع مبيعات المنصة نفسها، منفصل تمامًا عن CRMLeads داخل كل Tenant.

- **المفتاح الأساسي:** LeadId

#### `TenantSettings`  <sub>(platform · Config)</sub>

**الوظيفة:** إعدادات مفتاح/قيمة مرنة لكل Tenant، بدون الحاجة لتعديل الجداول عند إضافة إعداد جديد.

- **المفتاح الأساسي:** SettingId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Key
- **يرتبط بـ:** `Tenants`

#### `TenantProvisioningJobs`  <sub>(platform · Ops)</sub>

**الوظيفة:** تتبع عملية تجهيز قاعدة بيانات مستقلة لـ Tenant جديد (Pending/Creating/Migrating/Ready/Failed) عند اختيار Dedicated Database.

- **المفتاح الأساسي:** JobId
- **مفاتيح خارجية:** TenantId
- **يرتبط بـ:** `Tenants`

#### `TenantSchemaVersion`  <sub>(platform · Ops)</sub>

**الوظيفة:** نسخة الـ schema الحالية لكل Tenant — أساسي لمعرفة مين محتاج Migration جديد، خصوصًا مع تعدد قواعد البيانات المنفصلة.

- **المفتاح الأساسي:** TenantId
- **يرتبط بـ:** `Tenants`

#### `PlatformAuditLog`  <sub>(platform · Audit)</sub>

**الوظيفة:** سجل تدقيق لأي عملية إدارية على مستوى المنصة (تعديل خطة، تعليق Tenant، تغيير حدود).

- **المفتاح الأساسي:** LogId
- **مفاتيح خارجية:** TenantId
- **يرتبط بـ:** `Tenants` (index فقط)

---

### 🔐 المنصة — موظفو الإدارة الداخليين (Platform Staff)

#### `PlatformUsers`  <sub>(platform · Staff)</sub>

**الوظيفة:** موظفو المنصة (Super Admin/Sales/Support) — منفصل تمامًا عن جدول Users الخاص بمستخدمي الـ Tenants لأسباب أمنية وتنظيمية.

- **المفتاح الأساسي:** PlatformUserId
- **مفاتيح فريدة:** Email
- **جداول ترتبط به:** `PlatformUserRoles`, `ImpersonationLogs`

#### `PlatformRoles`  <sub>(platform · RBAC)</sub>

**الوظيفة:** أدوار داخلية لموظفي المنصة (SuperAdmin, SalesRep, SupportAgent, Billingmanager...).

- **المفتاح الأساسي:** RoleId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `PlatformUserRoles`, `PlatformRolePermissions`

#### `PlatformPermissions`  <sub>(platform · RBAC)</sub>

**الوظيفة:** صلاحيات دقيقة على مستوى المنصة، مستقلة تمامًا عن صلاحيات الـ Tenant.

- **المفتاح الأساسي:** PermissionId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `PlatformRolePermissions`

#### `PlatformUserRoles`  <sub>(platform · Junction)</sub>

**الوظيفة:** ربط موظف المنصة بأدواره.

- **مفاتيح خارجية:** PlatformUserId, RoleId
- **يرتبط بـ:** `PlatformUsers`, `PlatformRoles`

#### `PlatformRolePermissions`  <sub>(platform · Junction)</sub>

**الوظيفة:** ربط دور المنصة بصلاحياته.

- **مفاتيح خارجية:** RoleId, PermissionId
- **يرتبط بـ:** `PlatformRoles`, `PlatformPermissions`

#### `ImpersonationLogs`  <sub>(platform · Audit)</sub>

**الوظيفة:** توثيق إلزامي لكل مرة موظف دعم يدخل بالنيابة عن مستخدم Tenant لأغراض المساعدة الفنية.

- **المفتاح الأساسي:** LogId
- **مفاتيح خارجية:** PlatformUserId, TenantId
- **يرتبط بـ:** `PlatformUsers`, `Tenants` (index فقط)

---

### 🔑 Tenant — M-12 الأمان والصلاحيات

#### `Users`  <sub>(t_{slug} (Tenant Schema) · M-12)</sub>

**الوظيفة:** مستخدمو النظام داخل المركز (طالب/معلم/ولي أمر/موظف حسب UserType)، مع LinkedEntityId للربط بالكيان الفعلي. UK على Email مركّبة فعليًا مع TenantId.

- **المفتاح الأساسي:** UserId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Email
- **جداول ترتبط به:** `UserRoles`, `RefreshTokens`, `AuditLog`, `LoginHistory`, `Branches`, `Teachers`, `Employees`, `Notifications`, `NotificationRecipients`, `Parents`

#### `Roles`  <sub>(t_{slug} (Tenant Schema) · M-12)</sub>

**الوظيفة:** أدوار قابلة للتخصيص داخل كل مركز على حدة (يُزرع منها مجموعة افتراضية عند إنشاء أي Tenant جديد).

- **المفتاح الأساسي:** RoleId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `UserRoles`, `RolePermissions`

#### `Permissions`  <sub>(platform · Catalog)</sub>

**الوظيفة:** كتالوج صلاحيات عام تعرّفه المنصة (ثابت بالكود)، تُبنى منه أدوار كل Tenant — بدون TenantId لأنه تعريف نظامي وليس بيانات عميل.

- **المفتاح الأساسي:** PermissionId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `RolePermissions`

#### `UserRoles`  <sub>(t_{slug} (Tenant Schema) · Junction)</sub>

**الوظيفة:** ربط مستخدم Tenant بأدواره.

- **مفاتيح خارجية:** TenantId, UserId, RoleId
- **يرتبط بـ:** `Users`, `Roles`

#### `RolePermissions`  <sub>(t_{slug} (Tenant Schema) · Junction)</sub>

**الوظيفة:** ربط دور Tenant بصلاحياته المفعّلة من كتالوج Permissions العام.

- **مفاتيح خارجية:** TenantId, RoleId, PermissionId
- **يرتبط بـ:** `Roles`, `Permissions`

#### `RefreshTokens`  <sub>(t_{slug} (Tenant Schema) · Auth)</sub>

**الوظيفة:** توكنات تجديد الجلسة، بسلسلة استبدال قابلة للتتبع (ReplacedByTokenId).

- **المفتاح الأساسي:** TokenId
- **مفاتيح خارجية:** TenantId, UserId, ReplacedByTokenId
- **مفاتيح فريدة:** TokenHash
- **يرتبط بـ:** `Users`

#### `AuditLog`  <sub>(t_{slug} (Tenant Schema) · Audit)</sub>

**الوظيفة:** سجل تدقيق عام لكل العمليات الحساسة داخل بيانات المركز.

- **المفتاح الأساسي:** LogId
- **مفاتيح خارجية:** TenantId, UserId
- **يرتبط بـ:** `Users`

#### `LoginHistory`  <sub>(t_{slug} (Tenant Schema) · Audit)</sub>

**الوظيفة:** سجل كل محاولات الدخول (ناجحة وفاشلة) لأغراض الأمان.

- **المفتاح الأساسي:** LoginHistoryId
- **مفاتيح خارجية:** TenantId, UserId
- **يرتبط بـ:** `Users`

---

### 🎓 Tenant — M-01 الطلاب

#### `Branches`  <sub>(t_{slug} (Tenant Schema) · M-01)</sub>

**الوظيفة:** فروع المركز الفعلية. تُحسب ضمن BranchesCount مقابل حد MaxBranches في الخطة.

- **المفتاح الأساسي:** BranchId
- **مفاتيح خارجية:** TenantId, ManagerId
- **يرتبط بـ:** `Users` (index فقط)
- **جداول ترتبط به:** `Students`, `Rooms`, `Groups`, `Expenses`, `Employees`

#### `AcademicStages`  <sub>(t_{slug} (Tenant Schema) · M-01)</sub>

**الوظيفة:** المراحل الدراسية (ابتدائي/إعدادي/ثانوي...) الخاصة بالمركز.

- **المفتاح الأساسي:** StageId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `AcademicYears`, `Students`, `Subjects`, `Groups`

#### `AcademicYears`  <sub>(t_{slug} (Tenant Schema) · M-01)</sub>

**الوظيفة:** السنوات الدراسية داخل كل مرحلة.

- **المفتاح الأساسي:** YearId
- **مفاتيح خارجية:** TenantId, StageId
- **مفاتيح فريدة:** YearCode
- **يرتبط بـ:** `AcademicStages`
- **جداول ترتبط به:** `Students`

#### `Students`  <sub>(t_{slug} (Tenant Schema) · M-01 Core)</sub>

**الوظيفة:** الكيان المركزي للطالب. يُحسب ضمن StudentsCount مقابل حد MaxStudents في الخطة. RowVersion لحل تعارضات التزامن.

- **المفتاح الأساسي:** StudentId
- **مفاتيح خارجية:** TenantId, BranchId, StageId, YearId, CreatedBy, ModifiedBy, DeletedBy
- **مفاتيح فريدة:** QRCode
- **يرتبط بـ:** `Branches`, `AcademicStages`, `AcademicYears`
- **جداول ترتبط به:** `AttendanceLogs`, `TeacherRatings`, `StudentGroups`, `Waitlist`, `StudentFees`, `Payments`, `StudentCredits`, `AssessmentResults`, `CRMLeads`, `ReferralCodes`, `StudentReferrals`, `ChurnScores`, `AssignmentSubmissions`, `StudentProgress`, `StudentParents`

#### `AttendanceLogs`  <sub>(t_{slug} (Tenant Schema) · M-01)</sub>

**الوظيفة:** سجل حضور الطلاب، يدعم تسجيل Offline من الموبايل (IsOffline/SyncedAt) مع RowVersion لحل تعارضات المزامنة. GroupId اختياري لدعم حصص فردية خارج مجموعة.

- **المفتاح الأساسي:** AttendanceId
- **مفاتيح خارجية:** TenantId, StudentId, GroupId
- **يرتبط بـ:** `Students`, `Groups` (index فقط)

---

### 👨‍🏫 Tenant — M-02 المعلمون

#### `Subjects`  <sub>(t_{slug} (Tenant Schema) · M-02)</sub>

**الوظيفة:** المواد الدراسية المتاحة بالمركز.

- **المفتاح الأساسي:** SubjectId
- **مفاتيح خارجية:** TenantId, StageId
- **يرتبط بـ:** `AcademicStages` (index فقط)
- **جداول ترتبط به:** `Groups`, `QuestionBank`, `Courses`

#### `Teachers`  <sub>(t_{slug} (Tenant Schema) · M-02 Core)</sub>

**الوظيفة:** بيانات المعلمين. يُحسب ضمن TeachersCount مقابل حد MaxTeachers في الخطة.

- **المفتاح الأساسي:** TeacherId
- **مفاتيح خارجية:** TenantId, UserId, CreatedBy
- **يرتبط بـ:** `Users` (index فقط)
- **جداول ترتبط به:** `TeacherSalaryConfig`, `SalaryPayments`, `TeacherRatings`, `Groups`, `Courses`

#### `TeacherSalaryConfig`  <sub>(t_{slug} (Tenant Schema) · M-02)</sub>

**الوظيفة:** إعدادات احتساب راتب المعلم (نسبة/ثابت) لكل مجموعة أو عام.

- **المفتاح الأساسي:** ConfigId
- **مفاتيح خارجية:** TenantId, TeacherId, GroupId
- **يرتبط بـ:** `Teachers`, `Groups` (index فقط)

#### `SalaryPayments`  <sub>(t_{slug} (Tenant Schema) · M-02)</sub>

**الوظيفة:** مستحقات ودفعات رواتب المعلمين الشهرية.

- **المفتاح الأساسي:** PaymentId
- **مفاتيح خارجية:** TenantId, TeacherId
- **يرتبط بـ:** `Teachers`

#### `TeacherRatings`  <sub>(t_{slug} (Tenant Schema) · M-02)</sub>

**الوظيفة:** تقييمات الطلاب للمعلمين شهريًا.

- **المفتاح الأساسي:** RatingId
- **مفاتيح خارجية:** TenantId, TeacherId, StudentId, GroupId
- **يرتبط بـ:** `Teachers`, `Students`, `Groups` (index فقط)

---

### 🗓️ Tenant — M-03 الجدولة

#### `Rooms`  <sub>(t_{slug} (Tenant Schema) · M-03)</sub>

**الوظيفة:** قاعات/غرف كل فرع، تُستخدم لجدولة المجموعات.

- **المفتاح الأساسي:** RoomId
- **مفاتيح خارجية:** TenantId, BranchId
- **يرتبط بـ:** `Branches`
- **جداول ترتبط به:** `Groups`

#### `Groups`  <sub>(t_{slug} (Tenant Schema) · M-03 Core)</sub>

**الوظيفة:** المجموعات/الفصول الدراسية — الكيان المحوري الذي يربط المعلم بالطلاب بالجدول والتسعير.

- **المفتاح الأساسي:** GroupId
- **مفاتيح خارجية:** TenantId, BranchId, TeacherId, SubjectId, StageId, RoomId
- **يرتبط بـ:** `Branches`, `Teachers`, `Subjects`, `AcademicStages` (index فقط), `Rooms` (index فقط)
- **جداول ترتبط به:** `AttendanceLogs`, `TeacherSalaryConfig`, `TeacherRatings`, `GroupSchedule`, `StudentGroups`, `Waitlist`, `StudentFees`, `Assessments`, `ExamSessions`

#### `GroupSchedule`  <sub>(t_{slug} (Tenant Schema) · M-03)</sub>

**الوظيفة:** مواعيد انعقاد كل مجموعة أسبوعيًا.

- **المفتاح الأساسي:** ScheduleId
- **مفاتيح خارجية:** TenantId, GroupId
- **يرتبط بـ:** `Groups`

#### `StudentGroups`  <sub>(t_{slug} (Tenant Schema) · M-03)</sub>

**الوظيفة:** تسجيل الطلاب في المجموعات (Many-to-Many مع تاريخ الالتحاق/الانسحاب).

- **المفتاح الأساسي:** StudentGroupId
- **مفاتيح خارجية:** TenantId, StudentId, GroupId
- **يرتبط بـ:** `Students`, `Groups`

#### `Waitlist`  <sub>(t_{slug} (Tenant Schema) · M-03)</sub>

**الوظيفة:** قائمة انتظار للمجموعات المكتملة العدد.

- **المفتاح الأساسي:** WaitlistId
- **مفاتيح خارجية:** TenantId, GroupId, StudentId
- **يرتبط بـ:** `Groups`, `Students`

---

### 💰 Tenant — M-04 المالية

#### `FeeTypes`  <sub>(t_{slug} (Tenant Schema) · M-04)</sub>

**الوظيفة:** أنواع الرسوم (اشتراك شهري/رسوم تسجيل/كتب...).

- **المفتاح الأساسي:** FeeTypeId
- **مفاتيح خارجية:** TenantId
- **جداول ترتبط به:** `StudentFees`

#### `StudentFees`  <sub>(t_{slug} (Tenant Schema) · M-04)</sub>

**الوظيفة:** الرسوم المستحقة على كل طالب.

- **المفتاح الأساسي:** FeeId
- **مفاتيح خارجية:** TenantId, StudentId, GroupId, FeeTypeId
- **يرتبط بـ:** `Students`, `Groups` (index فقط), `FeeTypes`
- **جداول ترتبط به:** `Payments`, `StudentCredits`

#### `Payments`  <sub>(t_{slug} (Tenant Schema) · M-04)</sub>

**الوظيفة:** دفعات الطلاب الفعلية سداداً للرسوم — منفصل عن PlatformPayments الخاص باشتراك المركز نفسه في المنصة.

- **المفتاح الأساسي:** PaymentId
- **مفاتيح خارجية:** TenantId, FeeId, StudentId, CreatedBy
- **يرتبط بـ:** `StudentFees`, `Students`

#### `StudentCredits`  <sub>(t_{slug} (Tenant Schema) · M-04)</sub>

**الوظيفة:** محفظة أرصدة/خصومات للطالب (من مكافآت إحالة طالب لطالب، أو عروض)، تُطبَّق تلقائيًا على StudentFees القادمة.

- **المفتاح الأساسي:** CreditId
- **مفاتيح خارجية:** TenantId, StudentId, AppliedToFeeId
- **يرتبط بـ:** `Students`, `StudentFees` (index فقط)

#### `ExpenseCategories`  <sub>(t_{slug} (Tenant Schema) · M-04)</sub>

**الوظيفة:** تصنيفات المصروفات (إيجار/رواتب/صيانة...).

- **المفتاح الأساسي:** CategoryId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `Expenses`

#### `Expenses`  <sub>(t_{slug} (Tenant Schema) · M-04)</sub>

**الوظيفة:** مصروفات تشغيل كل فرع.

- **المفتاح الأساسي:** ExpenseId
- **مفاتيح خارجية:** TenantId, BranchId, CategoryId
- **يرتبط بـ:** `Branches`, `ExpenseCategories`

---

### 📝 Tenant — M-05 الأكاديمي (تقييمات وامتحانات)

#### `Assessments`  <sub>(t_{slug} (Tenant Schema) · M-05)</sub>

**الوظيفة:** اختبارات/تقييمات دورية لكل مجموعة.

- **المفتاح الأساسي:** AssessmentId
- **مفاتيح خارجية:** TenantId, GroupId
- **يرتبط بـ:** `Groups`
- **جداول ترتبط به:** `AssessmentResults`

#### `AssessmentResults`  <sub>(t_{slug} (Tenant Schema) · M-05)</sub>

**الوظيفة:** درجات الطلاب في كل تقييم.

- **المفتاح الأساسي:** ResultId
- **مفاتيح خارجية:** TenantId, AssessmentId, StudentId
- **يرتبط بـ:** `Assessments`, `Students`

#### `QuestionBank`  <sub>(t_{slug} (Tenant Schema) · M-05)</sub>

**الوظيفة:** بنك أسئلة قابل لإعادة الاستخدام في أي اختبار.

- **المفتاح الأساسي:** QuestionId
- **مفاتيح خارجية:** TenantId, SubjectId
- **يرتبط بـ:** `Subjects`

#### `ExamSessions`  <sub>(t_{slug} (Tenant Schema) · M-05)</sub>

**الوظيفة:** جلسات امتحان إلكترونية مجدولة.

- **المفتاح الأساسي:** ExamId
- **مفاتيح خارجية:** TenantId, GroupId
- **يرتبط بـ:** `Groups`

---

### 🧑‍💼 Tenant — M-06 الموارد البشرية

#### `Employees`  <sub>(t_{slug} (Tenant Schema) · M-06)</sub>

**الوظيفة:** الموظفون الإداريون (غير المعلمين) بكل فرع. يُحسب ضمن UsersCount إجمالاً مع بقية أنواع المستخدمين.

- **المفتاح الأساسي:** EmployeeId
- **مفاتيح خارجية:** TenantId, UserId, BranchId
- **يرتبط بـ:** `Users` (index فقط), `Branches`
- **جداول ترتبط به:** `LeaveRequests`

#### `LeaveTypes`  <sub>(t_{slug} (Tenant Schema) · M-06)</sub>

**الوظيفة:** أنواع الإجازات المسموح بها وحدها السنوي.

- **المفتاح الأساسي:** LeaveTypeId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `LeaveRequests`

#### `LeaveRequests`  <sub>(t_{slug} (Tenant Schema) · M-06)</sub>

**الوظيفة:** طلبات إجازة الموظفين.

- **المفتاح الأساسي:** RequestId
- **مفاتيح خارجية:** TenantId, EmployeeId, LeaveTypeId
- **يرتبط بـ:** `Employees`, `LeaveTypes`

---

### 🔔 Tenant — M-07 الاتصالات والإشعارات

#### `NotificationTemplates`  <sub>(t_{slug} (Tenant Schema) · M-07)</sub>

**الوظيفة:** قوالب الرسائل (SMS/Email/Push) الجاهزة لإعادة الاستخدام.

- **المفتاح الأساسي:** TemplateId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `NotificationLogs`

#### `NotificationLogs`  <sub>(t_{slug} (Tenant Schema) · M-07)</sub>

**الوظيفة:** سجل إرسال فعلي لكل رسالة SMS/Email — يُستخدم أيضًا لاحتساب SMSUsedThisCycle.

- **المفتاح الأساسي:** LogId
- **مفاتيح خارجية:** TenantId, TemplateId
- **يرتبط بـ:** `NotificationTemplates` (index فقط)

#### `Announcements`  <sub>(t_{slug} (Tenant Schema) · M-07)</sub>

**الوظيفة:** إعلانات عامة موجهة لفرع/مجموعة/الكل.

- **المفتاح الأساسي:** AnnouncementId
- **مفاتيح خارجية:** TenantId

#### `Notifications`  <sub>(t_{slug} (Tenant Schema) · M-07)</sub>

**الوظيفة:** إشعارات داخل التطبيق (In-app).

- **المفتاح الأساسي:** NotificationId
- **مفاتيح خارجية:** TenantId, CreatedBy
- **يرتبط بـ:** `Users` (index فقط)
- **جداول ترتبط به:** `NotificationRecipients`

#### `NotificationRecipients`  <sub>(t_{slug} (Tenant Schema) · Junction)</sub>

**الوظيفة:** من استلم/قرأ كل إشعار.

- **مفاتيح خارجية:** TenantId, NotificationId, UserId
- **يرتبط بـ:** `Notifications`, `Users`

---

### 🚀 Tenant — M-08 النمو والإحالات

#### `LeadSources`  <sub>(t_{slug} (Tenant Schema) · M-08)</sub>

**الوظيفة:** مصادر العملاء المحتملين (فيسبوك/إحالة/موقع...).

- **المفتاح الأساسي:** SourceId
- **مفاتيح خارجية:** TenantId
- **مفاتيح فريدة:** Code
- **جداول ترتبط به:** `CRMLeads`

#### `CRMLeads`  <sub>(t_{slug} (Tenant Schema) · M-08)</sub>

**الوظيفة:** عملاء محتملون لطلاب جدد داخل المركز — منفصل عن TenantCRMLeads (عملاء المنصة نفسها).

- **المفتاح الأساسي:** LeadId
- **مفاتيح خارجية:** TenantId, StudentId, SourceId
- **يرتبط بـ:** `Students` (index فقط), `LeadSources`

#### `ReferralCodes`  <sub>(t_{slug} (Tenant Schema) · M-08)</sub>

**الوظيفة:** كود إحالة ثابت لكل طالب.

- **المفتاح الأساسي:** CodeId
- **مفاتيح خارجية:** TenantId, StudentId
- **مفاتيح فريدة:** Code
- **يرتبط بـ:** `Students`
- **جداول ترتبط به:** `StudentReferrals`

#### `StudentReferrals`  <sub>(t_{slug} (Tenant Schema) · M-08)</sub>

**الوظيفة:** سجل إحالة طالب لطالب. نفس منطق TenantReferrals: المكافأة تُفعَّل بعد Qualified فقط (مثلاً أول قسط مدفوع).

- **المفتاح الأساسي:** ReferralId
- **مفاتيح خارجية:** TenantId, ReferrerStudentId, ReferralCodeId
- **مفاتيح فريدة:** ReferredStudentId
- **يرتبط بـ:** `Students` (index فقط), `ReferralCodes`

#### `ChurnScores`  <sub>(t_{slug} (Tenant Schema) · M-08)</sub>

**الوظيفة:** مؤشر احتمالية انسحاب الطالب (Churn Prediction) محسوب دوريًا.

- **المفتاح الأساسي:** ScoreId
- **مفاتيح خارجية:** TenantId, StudentId
- **يرتبط بـ:** `Students`

---

### 📚 Tenant — M-09 نظام التعلم الإلكتروني (LMS)

#### `Courses`  <sub>(t_{slug} (Tenant Schema) · M-09)</sub>

**الوظيفة:** دورات تعليمية إلكترونية (LMS).

- **المفتاح الأساسي:** CourseId
- **مفاتيح خارجية:** TenantId, TeacherId, SubjectId
- **يرتبط بـ:** `Teachers` (index فقط), `Subjects` (index فقط)
- **جداول ترتبط به:** `Units`, `Assignments`

#### `Units`  <sub>(t_{slug} (Tenant Schema) · M-09)</sub>

**الوظيفة:** وحدات الكورس.

- **المفتاح الأساسي:** UnitId
- **مفاتيح خارجية:** TenantId, CourseId
- **يرتبط بـ:** `Courses`
- **جداول ترتبط به:** `Lessons`

#### `Lessons`  <sub>(t_{slug} (Tenant Schema) · M-09)</sub>

**الوظيفة:** دروس الوحدة (فيديو/PDF/رابط).

- **المفتاح الأساسي:** LessonId
- **مفاتيح خارجية:** TenantId, UnitId
- **يرتبط بـ:** `Units`
- **جداول ترتبط به:** `StudentProgress`

#### `Assignments`  <sub>(t_{slug} (Tenant Schema) · M-09)</sub>

**الوظيفة:** واجبات الكورس.

- **المفتاح الأساسي:** AssignmentId
- **مفاتيح خارجية:** TenantId, CourseId
- **يرتبط بـ:** `Courses`
- **جداول ترتبط به:** `AssignmentSubmissions`

#### `AssignmentSubmissions`  <sub>(t_{slug} (Tenant Schema) · M-09)</sub>

**الوظيفة:** تسليمات الطلاب للواجبات.

- **المفتاح الأساسي:** SubmissionId
- **مفاتيح خارجية:** TenantId, AssignmentId, StudentId, FileId
- **يرتبط بـ:** `Assignments`, `Students`, `Files` (index فقط)

#### `StudentProgress`  <sub>(t_{slug} (Tenant Schema) · M-09)</sub>

**الوظيفة:** تقدم الطالب في مشاهدة/إكمال الدروس.

- **المفتاح الأساسي:** ProgressId
- **مفاتيح خارجية:** TenantId, StudentId, LessonId
- **يرتبط بـ:** `Students`, `Lessons`

---

### 👪 Tenant — M-10 أولياء الأمور

#### `Parents`  <sub>(t_{slug} (Tenant Schema) · M-10)</sub>

**الوظيفة:** أولياء الأمور.

- **المفتاح الأساسي:** ParentId
- **مفاتيح خارجية:** TenantId, UserId
- **مفاتيح فريدة:** Phone
- **يرتبط بـ:** `Users` (index فقط)
- **جداول ترتبط به:** `StudentParents`

#### `StudentParents`  <sub>(t_{slug} (Tenant Schema) · Junction)</sub>

**الوظيفة:** ربط الطالب بأولياء أموره (Many-to-Many).

- **مفاتيح خارجية:** TenantId, StudentId, ParentId
- **يرتبط بـ:** `Students`, `Parents`

---

### 🗂️ Tenant — M-11 التخزين والملفات

#### `Files`  <sub>(t_{slug} (Tenant Schema) · M-11)</sub>

**الوظيفة:** المرفقات المرفوعة. مجموع SizeBytes يُستخدم لاحتساب StorageUsedMB مقابل حد StorageGB بالخطة.

- **المفتاح الأساسي:** FileId
- **مفاتيح خارجية:** TenantId, UploadedBy
- **جداول ترتبط به:** `AssignmentSubmissions`, `EntityFiles`

#### `EntityFiles`  <sub>(t_{slug} (Tenant Schema) · Junction)</sub>

**الوظيفة:** ربط عام (Polymorphic) بين أي ملف وأي كيان في النظام (طالب/واجب/مركز...).

- **مفاتيح خارجية:** TenantId, FileId
- **يرتبط بـ:** `Files`

---

### 🗒️ Tenant — M-14 الملاحظات

#### `Notes`  <sub>(t_{slug} (Tenant Schema) · M-14)</sub>

**الوظيفة:** ملاحظات حرة (Polymorphic) على أي كيان.

- **المفتاح الأساسي:** NoteId
- **مفاتيح خارجية:** TenantId, CreatedBy

---

## 4) ملاحظات للمرحلة القادمة (مؤجَّلة عمدًا)

- **TenantDomains** (دومين مخصص/CNAME لعملاء كبار) — غير موجود في هذه النسخة، مؤجل لمرحلة Enterprise.
- **TenantSecurityPolicies** (SSO/SAML، IP Allowlist) — غير موجود في هذه النسخة، مؤجل لمرحلة Enterprise.
- **منظومة الفاتورة الإلكترونية (ETA):** جدول `Invoices` مُجهَّز بحقول أساسية (`InvoiceNumber`, `TaxAmount`) قابلة للتوسعة مستقبلًا بدون إعادة تصميم.
