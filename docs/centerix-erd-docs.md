# Centerix ERD v4 — Documentation
## Complete SaaS Multi-Tenant Educational Center Management System

---

## Overview

Centerix v4 is a **Hybrid Multi-Tenant SaaS** platform designed for educational centers (سناتر) in Egypt and the MENA region. It supports:

- **Shared Database** for small/medium tenants
- **Dedicated Database** for enterprise tenants
- **26 functional modules** covering every aspect of center management
- **106 database tables** with 130+ relationships
- **22 new tables** added in v4 for AI, Gamification, Marketplace, and advanced analytics

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    PLATFORM DATABASE                           │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────────┐     │
│  │ Tenants │──│  Plans  │──│Features │──│TenantPlans  │     │
│  └────┬────┘  └─────────┘  └─────────┘  └─────────────┘     │
│       │                                                        │
│       │  IsolationMode → Shared (0) أو Dedicated (1)         │
│       │                                                        │
│       │  ┌─────────────────────────────────────────────┐      │
│       └──►│ Dedicated DB per Tenant                    │      │
│           │  (Students, Teachers, Finance, HR...)      │      │
│           └─────────────────────────────────────────────┘      │
└─────────────────────────────────────────────────────────────────┘
```

---

## Module Index

| Module | Code | Tables | Description |
|--------|------|--------|-------------|
| Platform Core | — | 6 | Tenant management, plans, subscriptions |
| Platform Billing | — | 5 | Invoicing, payments, credits |
| Platform Growth | — | 6 | CRM, referrals, provisioning, audit |
| Platform Security | — | 6 | Staff RBAC, impersonation logs |
| Tenant Security | M-12 | 6 | User auth, roles, tokens, audit |
| Students | M-01 | 5 | Branches, stages, students, attendance |
| Teachers | M-02 | 5 | Subjects, teachers, salaries, ratings |
| Schedule | M-03 | 5 | Rooms, groups, timetables, waitlists |
| Finance | M-04 | 7 | Fees, payments, expenses, credits, pricing AI |
| Academic | M-05 | 6 | Assessments, question bank, exams, AI grading |
| HR | M-06 | 3 | Employees, leave types, leave requests |
| Communications | M-07 | 5 | Templates, logs, announcements, notifications |
| Growth / Referral | M-08 | 6 | Leads, referrals, churn prediction, signal factors |
| LMS | M-09 | 7 | Courses, units, lessons, assignments, live sessions |
| Parents | M-10 | 4 | Parents, student-parent links, action requests |
| Storage | M-11 | 2 | Files, entity attachments |
| Notes | M-14 | 1 | Free-form polymorphic notes |
| **Gamification** | **M-15** | **3** | **Badges, leaderboards, group challenges** ⭐ |
| **Marketplace** | **M-16** | **2** | **Teacher freelance marketplace** ⭐ |
| **Offline Sync** | **M-17** | **1** | **Offline-first conflict resolution** ⭐ |
| **Predictive Analytics** | **M-18** | **1** | **Revenue forecast, enrollment trends** ⭐ |
| **Digital Certificates** | **M-19** | **2** | **Blockchain-verified certificates** ⭐ |
| **Integrations** | **M-20** | **2** | **Payment gateways, WhatsApp, Zoom APIs** ⭐ |
| **Health & Safety** | **M-21** | **1** | **Incident tracking, parent alerts** ⭐ |
| **Feedback 360°** | **M-22** | **2** | **Multi-directional evaluations** ⭐ |
| **LiveOps** | **M-23** | **2** | **Real-time metrics and alerts** ⭐ |
| **AI Support** | **M-24** | **2** | **24/7 AI chatbot with knowledge base** ⭐ |
| **Student Evaluations** | **M-25** | **1** | **Teacher-to-student assessments** ⭐ |
| **Parent Alerts** | **M-26** | **1** | **Direct parent complaints & warnings** ⭐ |

---

## Table Reference (All 106 Tables)

### Platform — Core & Plans

#### `Tenants`
The central table for every subscribed center. The single connection point between Shared DB and Dedicated DB worlds via `IsolationMode`.

| Column | Type | Description |
|--------|------|-------------|
| TenantId | UNIQUEIDENTIFIER | Primary key |
| Slug / Subdomain | NVARCHAR | Center subdomain (centerix.com/slug) |
| IsolationMode | TINYINT | 0=Shared, 1=Dedicated DB |
| DatabaseServer | NVARCHAR | Dedicated DB server reference |
| CurrentPlanId | INT FK | Current subscription plan |
| LifecycleStatus | TINYINT | Active/Suspended/Cancelled |
| TrialEndsAt | DATETIME2 | Trial expiration |
| ValidUpTo | DATETIME2 | Subscription expiration |

---

#### `Plans`
Subscription plan catalog (Free/Starter/Pro/Enterprise). Defines default limits before any add-ons.

| Column | Type | Description |
|--------|------|-------------|
| PlanId | INT IDENTITY | Primary key |
| Code | NVARCHAR(30) | `free`, `starter`, `professional`, `enterprise` |
| MonthlyPrice | DECIMAL | Monthly subscription price |
| MaxStudents / MaxUsers / MaxBranches / MaxTeachers | INT | Hard limits |
| StorageGB / SMSQuota | INT | Resource quotas |

---

#### `Features`
Feature flags catalog. Enables/disables modules per plan dynamically without code deployment.

| Column | Type | Description |
|--------|------|-------------|
| FeatureId | INT IDENTITY | Primary key |
| Code | NVARCHAR(80) | `lms`, `growth_analytics`, `white_label_app`, `live_streaming`, `gamification` |
| Module | NVARCHAR(10) | Parent module code |

---

#### `PlanFeatures`
Junction table: which features are enabled in which plan.

---

#### `TenantPlans`
Historical subscription record per tenant. `SnapshotPrice` freezes the price at subscription time.

| Column | Type | Description |
|--------|------|-------------|
| SnapshotPrice | DECIMAL | Frozen price (immune to plan price changes) |
| AutoRenew | BIT | Automatic renewal flag |
| Status | TINYINT | active/cancelled/expired/upgraded |

---

#### `AddOnCatalog`
Add-on types purchasable on top of plans (extra branch, student block, extra user, SMS pack).

---

### Platform — Billing & Invoicing

#### `AddOnPricingTiers`
Volume-based pricing tiers per add-on type (quantity discounts).

---

#### `TenantAddOns`
Actual purchased add-ons per tenant. `EffectiveTo` set to end of paid cycle on cancellation (no partial refund).

---

#### `TenantUsageCounters`
Central usage counter updated periodically by Sync Job. Used for dashboard display and warnings — NOT for hard real-time blocking.

| Counter | Description |
|---------|-------------|
| StudentsCount / UsersCount / BranchesCount / TeachersCount | Current usage |
| StorageUsedMB / SMSUsedThisCycle | Resource consumption |
| EffectiveMax* | Calculated limit (plan + add-ons + overrides) |

---

#### `TenantLimitOverrides`
Custom limits above standard plan limits for enterprise deals with special agreements.

---

#### `Invoices`
Official invoice per subscription cycle. `InvoiceNumber` ready for e-invoice system expansion.

| Column | Type | Description |
|--------|------|-------------|
| InvoiceNumber | NVARCHAR(30) UK | Sequential invoice number |
| Subtotal / DiscountAmount / TaxAmount / TotalAmount | DECIMAL | Financial breakdown |
| Status | TINYINT | draft/issued/paid/overdue/cancelled |

---

#### `InvoiceLines`
Detailed invoice line items. `SourceType` distinguishes subscription/add-on/upgrade, `ProratedDays` documents mid-cycle pricing.

---

#### `PlatformPayments`
Actual platform-level payments (center subscription fees). Separate from student fee payments inside each center.

---

#### `TenantCredits`
Wallet/credit system for rewards, promotions, compensations. Automatically applied as discount on next invoice.

---

### Platform — Growth, CRM & Operations

#### `TenantReferralCodes`
Static referral code per center, shared with other potential centers.

---

#### `TenantReferrals`
Center-to-center referral record. Reward only activates after `Qualified` (e.g., first paid invoice) to prevent abuse.

---

#### `TenantCRMLeads`
Platform-level CRM leads (potential centers not yet subscribed). Separate from `CRMLeads` inside each tenant.

---

#### `TenantSettings`
Key-value flexible settings per tenant. No schema changes needed for new settings.

---

#### `TenantProvisioningJobs`
Tracks dedicated database provisioning for new tenants: Pending → Creating → Migrating → Ready → Failed.

---

#### `TenantSchemaVersion`
Current schema version per tenant. Essential for migration tracking across multiple separate databases.

---

#### `PlatformAuditLog`
Audit trail for all platform-level administrative operations (plan changes, tenant suspensions, limit changes).

---

### Platform — Internal Staff Security

#### `PlatformUsers`
Platform staff (Super Admin/Sales/Support). Completely separate from tenant users for security.

---

#### `PlatformRoles` / `PlatformPermissions` / `PlatformUserRoles` / `PlatformRolePermissions`
RBAC system for platform staff. Independent from tenant RBAC.

---

#### `ImpersonationLogs`
Mandatory logging every time support staff impersonates a tenant user. Records reason, duration, and IP address.

---

### Tenant — Security (M-12)

#### `Users`
All system users inside a center (student/teacher/parent/staff per `UserType`). `LinkedEntityId` links to actual entity record.

| Column | Type | Description |
|--------|------|-------------|
| UserType | NVARCHAR(20) | student / teacher / parent / staff |
| LinkedEntityId | UNIQUEIDENTIFIER | FK to actual entity table |
| FailedLoginCount | TINYINT | Brute-force protection |
| LockedUntil | DATETIME2 | Account lockout timestamp |

---

#### `Roles` / `Permissions` / `UserRoles` / `RolePermissions`
Tenant-level RBAC. `Permissions` is a system-wide catalog (no TenantId) — tenant roles reference it.

---

#### `RefreshTokens`
Session refresh tokens with replacement chain tracking (`ReplacedByTokenId`).

---

#### `AuditLog`
Tenant-level audit trail for all sensitive data operations.

---

#### `LoginHistory`
All login attempts (successful and failed) for security monitoring.

---

### Tenant — Students (M-01)

#### `Branches`
Physical center branches. Counted against `MaxBranches` plan limit.

---

#### `AcademicStages`
Academic stages (Primary/Preparatory/Secondary) per center.

---

#### `AcademicYears`
Academic years within each stage.

---

#### `Students`
Central student entity. Counted against `MaxStudents` plan limit.

| Column | Type | Description |
|--------|------|-------------|
| QRCode | NVARCHAR(100) UK | Unique barcode for attendance scanning |
| DiscountType / DiscountValue | — | Per-student fee discount |
| Status | TINYINT | active/suspended/graduated/transferred |
| RowVersion | ROWVERSION | Optimistic concurrency control |

---

#### `AttendanceLogs`
Student attendance with offline support. `IsOffline`/`SyncedAt` enable mobile offline recording. `RowVersion` resolves sync conflicts.

---

### Tenant — Teachers (M-02)

#### `Subjects`
Academic subjects offered by the center.

---

#### `Teachers`
Teacher records. Counted against `MaxTeachers` plan limit.

---

#### `TeacherSalaryConfig`
Salary calculation settings (percentage/fixed) per teacher per group.

---

#### `SalaryPayments`
Monthly teacher salary payments.

---

#### `TeacherRatings`
**Student-to-teacher ratings** (1-5 stars) per month. **Primary input for Churn Prediction** — declining ratings directly correlate with student attrition risk.

---

### Tenant — Schedule (M-03)

#### `Rooms`
Classrooms per branch with capacity and facilities.

---

#### `Groups`
Core entity linking teacher + students + subject + room + pricing.

---

#### `GroupSchedule`
Weekly recurring schedule per group (day of week + start/end time).

---

#### `StudentGroups`
Many-to-many enrollment with join/leave dates and status.

---

#### `Waitlist`
Waiting list for full-capacity groups with priority ranking.

---

### Tenant — Finance (M-04)

#### `FeeTypes`
Fee categories (monthly subscription, registration, books, etc.).

---

#### `StudentFees`
Fees due per student. `RowVersion` for concurrent modification protection.

---

#### `Payments`
Actual student fee payments. Separate from `PlatformPayments`.

---

#### `StudentCredits`
Student wallet for referral rewards and promotions. Auto-applied to upcoming fees.

---

#### `ExpenseCategories` / `Expenses`
Operational expense tracking per branch.

---

#### `PricingRecommendations` ⭐ NEW v4
AI-powered dynamic pricing suggestions per group based on occupancy, demand, teacher rating, and season.

| Column | Description |
|--------|-------------|
| SuggestedPrice | AI-recommended monthly price |
| ConfidenceScore | AI confidence (0-100) |
| Reasoning | Human-readable explanation |
| BasedOnData | JSON of data used for recommendation |

---

### Tenant — Academic (M-05)

#### `Assessments` / `AssessmentResults`
Periodic tests and student grades per group.

---

#### `QuestionBank`
Reusable question bank with difficulty, usage count, and correct rate tracking.

---

#### `ExamSessions`
Scheduled electronic exam sessions with duration and status.

---

#### `AutoGradingResults` ⭐ NEW v4
AI auto-grading for assignments and exams.

| Column | Description |
|--------|-------------|
| AIGrade | AI-assigned score |
| AIExplanation | Why this score? |
| ConfidenceLevel | high/medium/low |
| HumanOverride | Did a human correct the AI? |

**Supported question types:**
- Multiple choice: 100% accuracy
- True/False: 100% accuracy  
- Fill-in-blank: 95% accuracy
- Short essay: 70% accuracy (medium review)
- Long essay: 40% accuracy (requires human review)

**Impact:** Saves teachers 3-4 hours daily.

---

### Tenant — HR (M-06)

#### `Employees`
Administrative staff (non-teachers) per branch. Counted in total `UsersCount`.

---

#### `LeaveTypes` / `LeaveRequests`
Leave management with annual day limits.

---

### Tenant — Communications (M-07)

#### `NotificationTemplates`
Reusable message templates (SMS/Email/Push).

---

#### `NotificationLogs`
Actual sent message log. Used for `SMSUsedThisCycle` calculation.

---

#### `Announcements`
Targeted announcements (branch/group/all) with scheduling.

---

#### `Notifications` / `NotificationRecipients`
In-app notifications with read tracking.

---

### Tenant — Growth / Referral (M-08)

#### `LeadSources` / `CRMLeads`
Internal lead tracking for new student enrollment.

---

#### `ReferralCodes` / `StudentReferrals`
Student-to-student referral system with reward activation after qualification.

---

#### `ChurnScores`
Churn probability score (0-100) per student, computed periodically. `Signals` JSON documents risk factors.

---

#### `ChurnSignalFactors` ⭐ NEW v4
**Detailed churn risk factors** replacing generic JSON with structured data.

| Column | Description |
|--------|-------------|
| FactorType | teacher_rating_drop, absence_spike, payment_delay, grade_decline, parent_complaint |
| FactorSourceTable | Source table name |
| FactorSourceId | Source record ID |
| ImpactWeight | Effect on churn score (-10 to +10) |

**Example:** "Teacher Ahmed's rating dropped from 4.5 to 2.1 → +15 churn points"

---

### Tenant — LMS (M-09)

#### `Courses` / `Units` / `Lessons`
Online course structure with video/PDF/link content types and publish/expire dates.

---

#### `Assignments` / `AssignmentSubmissions`
Course assignments with file uploads and grading.

---

#### `StudentProgress`
Lesson completion tracking per student.

---

#### `LiveSessions` ⭐ NEW v4
Live streaming sessions for hybrid learning.

| Column | Description |
|--------|-------------|
| StreamUrl / RecordingUrl | Live and recorded session links |
| AttendeeCount / MaxAttendees | Capacity tracking |
| EngagementScore | Student interaction metric |
| Status | scheduled/live/completed/cancelled |

---

### Tenant — Parents (M-10)

#### `Parents`
Parent/guardian records with contact info.

---

#### `StudentParents`
Many-to-many link with relationship type and primary contact flag.

---

#### `ParentActionRequests` ⭐ NEW v4
Parent-initiated action requests.

| RequestType | Description |
|-------------|-------------|
| absence_excuse | Absence justification |
| grade_review | Grade dispute |
| teacher_change | Request different teacher |
| refund_request | Fee refund |
| payment_plan | Installment plan request |

---

### Tenant — Storage (M-11)

#### `Files` / `EntityFiles`
Polymorphic file attachment system. Total `SizeBytes` counted against `StorageGB` plan limit.

---

### Tenant — Notes (M-14)

#### `Notes`
Free-form polymorphic notes on any entity (student/teacher/group/invoice).

---

## ⭐ NEW MODULES IN v4

### M-15 Gamification

#### `StudentBadges`
Achievement badges for students: attendance streaks, top grades, homework hero, referrals.

| BadgeType | Description |
|-----------|-------------|
| attendance_streak | 30 days perfect attendance |
| top_grade | Highest score in group |
| homework_hero | 100% homework submission |
| referral | Referred new student |

**Impact:** Increases student engagement by estimated 30-40%.

---

#### `LeaderboardEntries`
Weekly/monthly/semester leaderboards per group with ranking.

---

#### `GroupChallenges`
Group-wide challenges (highest attendance rate, best average grade, etc.) with point rewards.

---

### M-16 Teacher Marketplace

#### `TeacherMarketplaceProfiles`
Internal freelance teacher marketplace.

| Column | Description |
|--------|-------------|
| HourlyRate | Per-session rate |
| AvailabilitySchedule | JSON days/hours |
| Specializations | "Math Secondary - Physics" |
| IsAvailableForHire | Open for booking |

**Business model:** Center hires teachers on-demand without monthly commitment. Centerix takes 5-10% commission per booking.

---

#### `TeacherSessionBookings`
Session bookings with agreed rate and payment status tracking.

---

### M-17 Offline Sync

#### `OfflineSyncQueue`
Offline-first conflict resolution queue.

| SyncStatus | Description |
|------------|-------------|
| pending | Waiting for internet |
| synced | Successfully uploaded |
| conflict | Requires manual resolution |
| failed | Error after retries |

**Conflict strategies:** Last-Write-Wins (default), Server-Wins (for payments), Manual-Review, Vector Clocks.

---

### M-18 Predictive Analytics

#### `PredictiveReports`
AI-generated predictive reports.

| ReportType | Prediction |
|------------|------------|
| revenue_forecast | 3-month revenue projection |
| enrollment_trend | Expected September enrollment |
| teacher_retention_risk | Teachers likely to resign |
| subject_demand | Most requested subjects upcoming |
| seasonal_churn | High-risk churn months |

---

### M-19 Digital Certificates

#### `DigitalCertificates`
Blockchain-verified digital certificates.

| Column | Description |
|--------|-------------|
| BlockchainTxHash | Ethereum/Polygon transaction hash |
| IPFSHash | Decentralized storage hash |
| QRVerificationUrl | Public verification link |
| RevokedAt / RevocationReason | Certificate revocation |

**Verification flow:** Any institution scans QR → calls Centerix API → verifies blockchain hash → displays original certificate data.

---

#### `CertificateTemplates`
Branded certificate templates per center with custom signatories.

---

### M-20 Integrations

#### `IntegrationConfigs`
Third-party integration settings.

| IntegrationType | Providers |
|-----------------|-----------|
| payment_gateway | Fawry, InstaPay, Vodafone Cash, Paymob |
| sms_provider | Twilio, MessageBird |
| whatsapp_api | WhatsApp Business API |
| live_streaming | Zoom, Google Meet, Jitsi |
| accounting | QuickBooks, Xero |
| bi_tool | Power BI, Tableau |

---

#### `WebhookDeliveryLog`
Webhook delivery tracking for external system notifications.

---

### M-21 Health & Safety

#### `HealthIncidents`
Health incident tracking with automatic parent notification.

| IncidentType | Severity |
|--------------|----------|
| illness | 2 |
| injury | 3 |
| allergy | 4 |
| emergency | 5 |

---

### M-22 Feedback 360°

#### `FeedbackCycles` / `FeedbackResponses`
Multi-directional evaluation system.

| Evaluator → Evaluatee | Purpose |
|-----------------------|---------|
| Student → Teacher | Teaching quality |
| Teacher → Student | Discipline & participation |
| Parent → Teacher | Communication & follow-up |
| Teacher → Teacher (Peer) | Collaboration & professionalism |
| Admin → Teacher | Job performance |
| Student → Student (Peer) | Teamwork |

---

### M-23 LiveOps

#### `LiveMetrics`
Real-time operational metrics dashboard.

| MetricType | Description |
|------------|-------------|
| active_students_now | Currently checked in |
| attendance_rate_today | Today's attendance % |
| revenue_today | Daily revenue |
| teacher_utilization | Classroom usage % |

---

#### `LiveAlerts`
Real-time operational alerts.

| AlertType | Trigger | Action |
|-----------|---------|--------|
| sudden_absence | 5+ absent in same group | Check teacher/room |
| low_revenue | Today < 50% of average | Send fee reminders |
| teacher_late | Not checked in +15 min | Alert management |
| churn_spike | 3+ high-risk this week | Emergency meeting |

---

### M-24 AI Support

#### `AIConversations`
24/7 AI support agent conversations.

| IntentDetected | AI Action |
|----------------|-----------|
| fee_inquiry | Calculate remaining fees |
| attendance_check | Report absence summary |
| teacher_info | Show teacher details |
| schedule_query | Send timetable |
| complaint | Route to appropriate manager |
| payment_link | Generate and send payment URL |
| document_request | Generate and send PDF |

---

#### `AIKnowledgeBase`
Self-learning knowledge base. `TenantId=NULL` = global knowledge. `TenantId` specified = center-specific knowledge.

---

### M-25 Student Evaluations

#### `StudentEvaluations`
**Teacher-to-student evaluations** with parent alert integration.

| Category | Weight | Alert Threshold |
|----------|--------|-----------------|
| academic | 0.40 | Score < 5 |
| behavior | 0.30 | Score < 4 |
| attendance | 0.20 | 3+ absences |
| participation | 0.10 | Score < 3 |

**Alert flow:** `IsAlert=1` → immediate notification to parent via WhatsApp/In-App → parent can reply → action required flag for admin.

---

### M-26 Parent Alerts

#### `ParentAlerts`
Direct parent communication channel for complaints and warnings.

| Category | Channel | Parent Can Reply |
|----------|---------|------------------|
| behavior | WhatsApp/Push | Yes |
| academic_drop | In-App/SMS | Yes |
| absence | WhatsApp | Yes |
| payment_overdue | SMS/Email | No |
| teacher_complaint | In-App | Yes |

| Status | Description |
|--------|-------------|
| pending | Waiting to send |
| sent | Delivered to parent app |
| delivered | Parent received notification |
| read | Parent opened |
| parent_replied | Parent responded |
| action_taken | Admin resolved |

---

## Key Design Decisions

### 1. Hybrid Multi-Tenant
- **Shared DB:** Default for Free/Starter/Pro plans. Lower cost, easier maintenance.
- **Dedicated DB:** For Enterprise plans. Data isolation, custom backups, compliance requirements.
- **Migration path:** Tenant can upgrade from Shared to Dedicated with zero downtime via `TenantProvisioningJobs`.

### 2. Feature Flags
- Every feature is a flag in `Features` table.
- Plans define which flags are enabled via `PlanFeatures`.
- Enables A/B testing, gradual rollouts, and plan customization without code changes.

### 3. Snapshot Pricing
- `TenantPlans.SnapshotPrice` freezes the price at subscription time.
- Protects existing customers from price increases.
- New customers pay current plan price.

### 4. Prorated Billing
- `InvoiceLines.ProratedDays` documents mid-cycle purchases.
- Fair billing: buy an add-on on day 15 → pay 50% of monthly price.

### 5. Optimistic Concurrency
- `RowVersion` on `Students`, `AttendanceLogs`, `StudentFees`, `StudentGroups`.
- Prevents lost updates in multi-user environments.

### 6. Polymorphic Design
- `Notes` and `EntityFiles` use `EntityType` + `EntityId` pattern.
- Single table serves all entity types without schema proliferation.

### 7. AI Integration Points
- **AutoGradingResults:** AI scores essays, humans review low-confidence results.
- **ChurnSignalFactors:** Structured risk factors feed into ML models.
- **PredictiveReports:** Revenue and enrollment forecasting.
- **PricingRecommendations:** Dynamic group pricing.
- **AIConversations:** 24/7 parent support.

---

## Churn Prediction Architecture

```
Data Sources:
├── TeacherRatings (student → teacher)
├── StudentEvaluations (teacher → student)  
├── ParentAlerts (complaints)
├── AttendanceLogs (absence patterns)
├── StudentFees (payment delays)
├── AssessmentResults (grade decline)
└── FeedbackResponses (360° scores)
         │
         ▼
   ChurnSignalFactors (structured factors with weights)
         │
         ▼
   ChurnScores (aggregated 0-100 score)
         │
         ▼
   LiveAlerts (real-time warnings to admin)
   ParentAlerts (proactive parent engagement)
   PricingRecommendations (retention offers)
```

| Risk Factor | Weight | Source Table |
|-------------|--------|--------------|
| Teacher rating drop < 3 | +15 | TeacherRatings |
| 3+ consecutive absences | +20 | AttendanceLogs |
| Payment overdue > 7 days | +25 | StudentFees |
| Grade decline > 20% | +10 | AssessmentResults |
| Parent complaint filed | +30 | ParentAlerts |
| Behavior alert from teacher | +12 | StudentEvaluations |
| Parent positive reply | -10 | ParentAlerts |

---

## Entity Relationship Summary

```
Tenants
├── TenantPlans → Plans
├── TenantAddOns → AddOnCatalog
├── Invoices → InvoiceLines
├── PlatformPayments → Invoices
├── TenantCredits → InvoiceLines
├── TenantReferrals → TenantReferralCodes
├── TenantSettings
├── TenantProvisioningJobs
└── TenantSchemaVersion

Users (per tenant)
├── UserRoles → Roles → RolePermissions → Permissions
├── RefreshTokens
├── AuditLog
└── LoginHistory

Students
├── AttendanceLogs
├── StudentFees → FeeTypes
├── Payments → StudentFees
├── StudentCredits
├── AssessmentResults → Assessments
├── StudentGroups → Groups
├── StudentParents → Parents
├── StudentProgress → Lessons
├── AssignmentSubmissions → Assignments
├── StudentBadges
├── LeaderboardEntries
├── ChurnScores
└── DigitalCertificates → CertificateTemplates

Teachers
├── TeacherSalaryConfig → Groups
├── SalaryPayments
├── TeacherRatings → Students, Groups
├── TeacherMarketplaceProfiles
└── TeacherSessionBookings → Groups

Groups
├── GroupSchedule
├── StudentGroups → Students
├── Waitlist → Students
├── Assessments
├── ExamSessions
├── Assignments
└── PricingRecommendations

Courses (LMS)
├── Units
├── Lessons
├── Assignments
└── LiveSessions
```

---

*Generated for Centerix v4 — Complete SaaS Educational Center Management System*
*Total Tables: 106 | New in v4: 22 | Modules: 26*
