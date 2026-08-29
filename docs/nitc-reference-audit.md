# NITC CanteenPay Reference Visual Audit

**Reference URL**: [https://nitc-canteenpay.webflow.io/](https://nitc-canteenpay.webflow.io/)  
**Target Application**: TrustFlow (N-Cash) Financial Platform  
**Audit Date**: August 2026

---

## 1. Reference Canvas & Layout Geometry

| Region | Observed Value | Implementation Token / Target |
| :--- | :--- | :--- |
| **Desktop Viewport Inspected** | 1440 × 900 px | Primary responsive baseline |
| **Page Background** | `#F4F7FB` (soft ice-blue gray) | `--nitc-bg-page: #F4F7FB;` |
| **Top Navigation Banner Height** | `70px` (fixed top header) | `--nitc-header-height: 70px;` |
| **Sidebar Width (Compact Tray)** | `84px` | `--nitc-sidebar-compact: 84px;` |
| **Sidebar Width (Expanded)** | `240px` | `--nitc-sidebar-width: 240px;` |
| **Main Content Container** | Max-width `1200px`, margin auto | `.app-container` |
| **Card Grid Gap** | `24px` horizontal & vertical | `--nitc-grid-gap: 24px;` |
| **Main Content Padding** | `32px 36px` | `--nitc-content-padding: 32px 36px;` |

---

## 2. Color Palette & Grading

| Color Role | Hex / RGBA Code | Usage in NITC CanteenPay |
| :--- | :--- | :--- |
| **Primary Navy Accent** | `#1B4F9B` | Primary action buttons, brand headers, active icons, step circles |
| **Primary Accent Hover** | `#153E7A` | Hover fill on primary buttons and active indicators |
| **Primary Accent Soft** | `#EAF2FD` | Selected navigation items, badge backgrounds, input focus halos |
| **Secondary Blue** | `#3898EC` | Links, focus outlines, secondary action pills |
| **Page Background** | `#F4F7FB` | Main viewport canvas behind all cards and forms |
| **Card / Surface Background**| `#FFFFFF` | Form containers, action cards, top header, side tray |
| **Subtle Border** | `#E2E8F0` / `#D9E2EC` | Card borders, input field borders, table dividers |
| **Text Primary (Headings)** | `#1E293B` / `#0F172A` | H1 titles, card headings, strong amounts |
| **Text Secondary (Body)** | `#475569` | Form labels, description paragraphs, list content |
| **Text Muted (Captions)** | `#94A3B8` | Timestamps, placeholders, inactive icons |
| **Success / Verified** | `#00B894` / `#10B981` | Completed checkmarks, zero-variance ledger proofs, green chips |
| **Warning / Step-Up** | `#F59E0B` | Medium risk shield warnings, pending money requests |
| **Danger / High-Risk** | `#EA384C` / `#EF4444` | High-risk step-up triggers, invalid attempt alerts, rejection buttons |

---

## 3. Typography Hierarchy

- **Primary Font Family**: `'Droid Sans', 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif`
- **Weights Loaded**: `400 (Regular)`, `600 (Semi-Bold)`, `700 (Bold)`

| Element | Font Size | Font Weight | Line Height | Color |
| :--- | :--- | :--- | :--- | :--- |
| **Login Title (`.heading-text-login`)** | `32px` | `700 (Bold)` | `38px` | `#1B4F9B` |
| **Login Subtitle** | `15px` | `400 (Regular)` | `22px` | `#64748B` |
| **Page / Section Header** | `24px` | `700 (Bold)` | `30px` | `#1E293B` |
| **Action Card Title** | `18px` | `700 (Bold)` | `24px` | `#1B4F9B` |
| **Balance Currency Amount** | `36px` | `700 (Bold)` | `42px` | `#0F172A` |
| **Form Label** | `13px` | `600 (Semi-Bold)` | `18px` | `#475569` |
| **Input / Button Text** | `14px` | `600 (Semi-Bold)` | `20px` | `#1E293B` / `#FFFFFF` |
| **Table Header / Small Label** | `12px` | `700 (Bold)` | `16px` | `#94A3B8` (Uppercase tracking) |
| **Badge / Tag Text** | `11px` | `700 (Bold)` | `14px` | Contextual |

---

## 4. Component Geometry & Metrics

| Component | Dimensions & Properties |
| :--- | :--- |
| **Primary Pill Button (`.w-button`)** | Height: `46px`, Padding: `12px 32px`, Radius: `24px`, Background: `#1B4F9B`, Text: White Bold, Shadow: `0 4px 14px rgba(27, 79, 155, 0.25)` |
| **Secondary Button** | Height: `42px`, Padding: `10px 24px`, Radius: `22px`, Background: `#EAF2FD`, Color: `#1B4F9B`, Border: `none` |
| **Ghost / Outline Button** | Height: `40px`, Padding: `8px 20px`, Radius: `20px`, Border: `1.5px solid #D9E2EC`, Background: Transparent |
| **Form Inputs (`.w-input`)** | Height: `46px`, Padding: `10px 16px (or 44px with left icon)`, Radius: `8px`, Border: `1.5px solid #D9E2EC`, Background: `#FFFFFF` |
| **Action Hub Cards (`.card-register`)**| Padding: `24px 28px`, Radius: `16px`, Background: `#FFFFFF`, Border: `1px solid #E8EEF5`, Shadow: `0 6px 20px rgba(27, 79, 155, 0.06)` |
| **Top Navigation Bar** | Height: `70px`, Background: `#FFFFFF`, Border-bottom: `1px solid #E2E8F0`, Shadow: `0 2px 8px rgba(0,0,0,0.03)` |
| **Step Indicator Circle** | Diameter: `54px`, Radius: `50%`, Background: `#EAF2FD` (Active: `#1B4F9B` with white icon) |
| **Table / Activity Rows** | Min-height: `58px`, Padding: `14px 20px`, Border-bottom: `1px solid #F1F5F9` |
| **Modal Dialog Containers** | Width: `560px` (Standard) / `780px` (Wide), Radius: `18px`, Padding: `32px`, Shadow: `0 20px 45px rgba(15, 23, 42, 0.16)` |

---

## 5. Visual Silhouette & Page Structures

1. **Login & Register**:
   - Clean split card container centered on `#F4F7FB` background.
   - Left side: High-contrast white login form card with blue branding, input icon prefixes (user & lock), and pill button.
   - Right side: Decorative NITC vector curves in navy and sky blue with TrustFlow reliability badge.
2. **Dashboard**:
   - Header with brand crest, home icon, and profile dropdown ("Welcome {User}").
   - Left vertical navigation tray with icon buttons.
   - Hero balance card showing verified ledger balance and quick action pills.
   - 2x2 grid of action cards with custom line artwork (Send Money, Request Money, Activity & Receipts, Trust Lab & Groups).
   - Pending money requests & recent activity list in clean rounded white surfaces.
3. **Send Money & Multi-Step Flows**:
   - 3-step circular progress bar (`Recipient` $\to$ `Amount & Risk` $\to$ `Review & Confirm`).
   - Dynamic Risk Shield indicator card displaying live deterministic score additions.
   - Circular success confirmation screen with "+ New Transfer" and "Done" actions.
4. **Transaction Detail & Trust Receipt**:
   - Clean printable ticket format with zero-variance ledger verification badge ($\Delta = 0.00$), 10-step immutable timeline, and double-entry debits/credits breakdown.
5. **Trust Lab**:
   - Grid of interactive test cards (Duplicate test, Concurrency test, Lost response retry, System Ledger Audit) with instant visual pass/fail badges.
