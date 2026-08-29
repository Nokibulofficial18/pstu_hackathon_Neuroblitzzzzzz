/**
 * TrustFlow (N-Cash) — NITC CanteenPay Client Architecture
 * All API calls target the correct C# / ASP.NET Core endpoints.
 */

const API_BASE = '/api';
let authToken    = localStorage.getItem('tf_token');
let currentUser  = null;
let currentPage  = 1;
let currentTab   = 'incoming';   // 'incoming' | 'outgoing'

// Send-money wizard state
let sendState = {
    recipientId:      null,
    recipientDisplay: null,
    amount:           null,
    purpose:          null,
    idempotencyKey:   null,
    riskAssessment:   null
};

// Pay-request modal state
let payReqState = { id: null, maxAmount: null };

/* ============================================================
   UTILITIES
   ============================================================ */
function generateUUID() {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
        const r = Math.random() * 16 | 0;
        return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
    });
}

function fmtBDT(v) {
    const num = parseFloat(v || 0);
    return '৳ ' + num.toLocaleString('en-BD', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function fmtDate(d) {
    if (!d) return '—';
    return new Date(d).toLocaleString('en-BD', {
        year: 'numeric', month: 'short', day: 'numeric',
        hour: '2-digit', minute: '2-digit'
    });
}

function statusBadge(st) {
    const m = {
        'COMPLETED':     ['badge-success', '✓ Completed'],
        'COMPLETED_IDEMPOTENT': ['badge-success', '♻ Deduplicated'],
        'PROCESSING':    ['badge-warning', '⏳ Processing'],
        'FAILED':        ['badge-danger',  '✗ Failed'],
        'ROLLED_BACK':   ['badge-danger',  '↩ Rolled Back'],
        'UNKNOWN':       ['badge-neutral', '? Unknown'],
        'PENDING':       ['badge-warning', '⏳ Pending'],
        'PARTIALLY_PAID':['badge-warning', '≈ Partial'],
        'PAID':          ['badge-success', '✓ Paid'],
        'REJECTED':      ['badge-danger',  '✗ Rejected'],
        'CANCELLED':     ['badge-neutral', '✗ Cancelled'],
        'ACTIVE':        ['badge-info',    '● Active'],
        'OPEN':          ['badge-info',    '● Open'],
        'UNDER_INVESTIGATION': ['badge-warning', '🔍 Investigating'],
        'RESOLVED':      ['badge-success', '✓ Resolved'],
        'CLOSED':        ['badge-neutral', 'Closed'],
    };
    const [cls, label] = m[st] || ['badge-neutral', st];
    return `<span class="badge ${cls}">${label}</span>`;
}

function riskBadge(score) {
    if (score <= 30)  return `<span class="badge badge-success">Score: ${score} (LOW)</span>`;
    if (score <= 60)  return `<span class="badge badge-warning">Score: ${score} (MEDIUM)</span>`;
    return `<span class="badge badge-danger">Score: ${score} (HIGH)</span>`;
}

function showAlert(id, msg) {
    const el = document.getElementById(id);
    if (!el) return;
    el.textContent = msg;
    el.style.display = 'block';
}
function hideAlert(id) {
    const el = document.getElementById(id);
    if (el) el.style.display = 'none';
}

function setBtnLoading(id, msg = 'Processing...', disabled = true) {
    const el = document.getElementById(id);
    if (!el) return;
    el.disabled = disabled;
    if (msg) el.textContent = msg;
}

/* ============================================================
   API REQUEST HELPER
   ============================================================ */
async function apiRequest(endpoint, method = 'GET', body = null, extraHeaders = {}) {
    const headers = {
        'Content-Type': 'application/json',
        ...extraHeaders
    };
    if (authToken) headers['Authorization'] = `Bearer ${authToken}`;

    const opts = { method, headers };
    if (body) opts.body = JSON.stringify(body);

    try {
        const res  = await fetch(`${API_BASE}${endpoint}`, opts);
        const data = await res.json();
        if (!res.ok) {
            if (res.status === 401 && !endpoint.includes('/auth/login')) {
                handleLogout();
                throw new Error('Session expired. Please sign in again.');
            }
            throw new Error(data.message || data.detail || data.title || 'Request failed.');
        }
        return data;
    } catch (err) {
        console.error(`[TrustFlow] ${method} ${endpoint}`, err);
        throw err;
    }
}

/* ============================================================
   INITIALIZATION & ROUTING
   ============================================================ */
document.addEventListener('DOMContentLoaded', () => {
    window.addEventListener('hashchange', handleRoute);
    if (authToken) {
        bootstrapUser();
    } else {
        showAuthScreen();
    }
});

function handleRoute() {
    if (!authToken) { showAuthScreen(); return; }
    const hash = window.location.hash.replace('#', '') || 'dashboard';
    navigateTo(hash, false);
}

function navigateTo(route, updateHash = true) {
    if (updateHash) window.location.hash = route;

    document.querySelectorAll('.app-view').forEach(el => el.style.display = 'none');

    document.querySelectorAll('.nav-link').forEach(el => {
        el.classList.toggle('active', el.dataset.nav === route);
    });

    const section = document.getElementById(`section-${route}`);
    if (section) section.style.display = 'block';

    switch (route) {
        case 'dashboard': loadDashboard(); break;
        case 'send':      resetSendWizard(); break;
        case 'requests':  loadRequests(); break;
        case 'groups':    loadGroups(); break;
        case 'activity':  loadActivity(currentPage); break;
        case 'recovery':  loadRecovery(); break;
        case 'profile':   loadProfile(); break;
    }
}

function toggleSidebar() {
    const sb = document.getElementById('sidebar');
    if (window.innerWidth <= 991) {
        sb.classList.toggle('mobile-open');
    } else {
        sb.classList.toggle('expanded');
    }
}

/* ============================================================
   AUTH LIFECYCLE
   ============================================================ */
async function bootstrapUser() {
    try {
        const res = await apiRequest('/auth/me');
        currentUser = res.data;
        showAppShell();
        handleRoute();
    } catch {
        handleLogout();
    }
}

function showAuthScreen() {
    document.getElementById('auth-container').style.display = 'flex';
    document.getElementById('app-shell').style.display = 'none';
}

function showAppShell() {
    document.getElementById('auth-container').style.display = 'none';
    document.getElementById('app-shell').style.display = 'block';
    if (currentUser) {
        const name = currentUser.fullName || currentUser.username || 'User';
        document.getElementById('hdr-welcome').textContent = `Welcome ${name}`;
        document.getElementById('hdr-avatar').textContent   = name[0].toUpperCase();
    }
}

function switchAuthView(view) {
    document.getElementById('view-login').style.display    = view === 'login'    ? 'flex' : 'none';
    document.getElementById('view-register').style.display = view === 'register' ? 'flex' : 'none';
}

async function handleLogin(e) {
    e.preventDefault();
    hideAlert('login-alert');
    const btn = document.getElementById('btn-login');
    btn.disabled = true; btn.textContent = 'Signing in...';

    try {
        const res = await apiRequest('/auth/login', 'POST', {
            usernameOrEmail: document.getElementById('login-username').value.trim(),
            password:        document.getElementById('login-password').value
        });
        authToken   = res.data.token;
        localStorage.setItem('tf_token', authToken);
        currentUser = {
            id:            res.data.userId,
            username:      res.data.username,
            fullName:      res.data.fullName,
            accountNumber: res.data.accountNumber
        };
        showAppShell();
        navigateTo('dashboard');
    } catch (err) {
        showAlert('login-alert', err.message);
    } finally {
        btn.disabled = false;
        btn.innerHTML = `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><path d="M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4"/><polyline points="10 17 15 12 10 7"/><line x1="15" y1="12" x2="3" y2="12"/></svg><span>Login</span>`;
    }
}

async function handleRegister(e) {
    e.preventDefault();
    hideAlert('register-alert');
    const btn = document.getElementById('btn-register');
    btn.disabled = true; btn.textContent = 'Creating Account...';

    try {
        const res = await apiRequest('/auth/register', 'POST', {
            fullName:    document.getElementById('reg-name').value.trim(),
            username:    document.getElementById('reg-username').value.trim(),
            phoneNumber: document.getElementById('reg-phone').value.trim(),
            email:       document.getElementById('reg-email').value.trim(),
            password:    document.getElementById('reg-password').value
        });
        authToken   = res.data.token;
        localStorage.setItem('tf_token', authToken);
        currentUser = {
            id:            res.data.userId,
            username:      res.data.username,
            fullName:      res.data.fullName,
            accountNumber: res.data.accountNumber
        };
        showAppShell();
        navigateTo('dashboard');
    } catch (err) {
        showAlert('register-alert', err.message);
    } finally {
        btn.disabled = false;
        btn.textContent = `Create Account & Claim ৳100,000`;
    }
}

function handleLogout() {
    authToken   = null;
    currentUser = null;
    localStorage.removeItem('tf_token');
    window.location.hash = '';
    showAuthScreen();
    switchAuthView('login');
}

function quickLogin(username) {
    document.getElementById('login-username').value = username;
    document.getElementById('login-password').value = 'Password123!';
}

/* ============================================================
   DASHBOARD
   ============================================================ */
async function loadDashboard() {
    try {
        // Wallet balance
        const wallet = await apiRequest('/wallet');
        document.getElementById('dash-balance').textContent = fmtBDT(wallet.data.balance);
    } catch { /* silently keep last value */ }

    // Pending incoming requests
    try {
        const reqs = await apiRequest('/requests/incoming');
        const list  = (reqs.data || []).filter(r => r.status === 'PENDING').slice(0, 4);
        const el    = document.getElementById('dash-pending');
        el.innerHTML = list.length ? list.map(r => `
            <div class="req-row">
                <div>
                    <div class="font-bold" style="font-size:13px;">${r.requesterUsername || '—'} <span class="text-muted font-sm">asks</span></div>
                    <div style="font-size:13px;font-weight:700;color:var(--primary);">${fmtBDT(r.remainingAmount)}</div>
                    <div class="font-xs text-muted">${r.note || ''}</div>
                </div>
                <div class="req-row-actions">
                    ${statusBadge(r.status)}
                    <button class="btn btn-primary btn-xs" onclick="openPayReqModal('${r.id}','${r.remainingAmount}','${r.requesterUsername}')">Pay</button>
                </div>
            </div>`).join('')
            : `<div class="empty-state">No pending requests 🎉</div>`;
    } catch { document.getElementById('dash-pending').innerHTML = `<div class="empty-state">Could not load requests.</div>`; }

    // Recent transactions
    try {
        const txns = await apiRequest('/transfers?page=1&pageSize=4');
        const el   = document.getElementById('dash-recent');
        const list = txns.data || [];
        el.innerHTML = list.length ? list.map(t => {
            const isOut = t.senderUserId === currentUser?.id;
            return `
            <div class="req-row">
                <div>
                    <div class="font-bold" style="font-size:13px;">${isOut ? t.recipientUsername : t.senderUsername} • <span class="text-muted font-sm">${t.purpose || ''}</span></div>
                    <div class="font-xs text-muted">${fmtDate(t.createdAt)}</div>
                </div>
                <div class="${isOut ? 'amt-out' : 'amt-in'}">${isOut ? '−' : '+'}${fmtBDT(t.amount)}</div>
            </div>`;
        }).join('')
            : `<div class="empty-state">No transactions yet.</div>`;
    } catch { document.getElementById('dash-recent').innerHTML = `<div class="empty-state">Could not load activity.</div>`; }
}

/* ============================================================
   SEND MONEY WIZARD
   ============================================================ */
let recipientLookupTimer = null;

function resetSendWizard() {
    sendState = { recipientId: null, recipientDisplay: null, amount: null, purpose: null, idempotencyKey: null, riskAssessment: null };
    goStep(1);
    hideAlert('send-alert');
    document.getElementById('send-recipient').value = '';
    document.getElementById('recipient-preview').textContent = '';
    document.getElementById('send-amount').value  = '';
    document.getElementById('send-purpose').value = '';
    document.getElementById('risk-badge').className = 'badge badge-info';
    document.getElementById('risk-badge').textContent = 'Score: — (PENDING)';
    document.getElementById('risk-reasons').textContent = 'Enter amount to calculate risk.';
}

function goStep(n) {
    // Validation guards
    if (n === 2 && !sendState.recipientId) {
        showAlert('send-alert', 'Please search for and select a valid recipient before continuing.');
        return;
    }
    if (n === 3) {
        const amount = parseFloat(document.getElementById('send-amount').value);
        if (!amount || amount <= 0) {
            showAlert('send-alert', 'Please enter a valid transfer amount.');
            return;
        }
        hideAlert('send-alert');
    } else {
        hideAlert('send-alert');
    }

    [1, 2, 3].forEach(i => {
        const s  = document.getElementById(`step${i}`);
        const ind = document.getElementById(`step${i}-ind`);
        if (!s || !ind) return;
        s.style.display = i === n ? 'block' : 'none';
        ind.className = 'indicator-step' + (i === n ? ' active' : (i < n ? ' done' : ''));
    });
    const l1 = document.getElementById('line1');
    const l2 = document.getElementById('line2');
    if (l1) l1.classList.toggle('done', n > 1);
    if (l2) l2.classList.toggle('done', n > 2);

    if (n === 3) buildReview();
}

// Quick select recipient from suggestion pills
function selectRecipient(accOrUsername, displayText) {
    document.getElementById('send-recipient').value = accOrUsername;
    sendState.recipientId      = accOrUsername;
    sendState.recipientDisplay = displayText;
    document.getElementById('recipient-preview').textContent = `✓ ${displayText}`;
    clearTimeout(recipientLookupTimer);
}

// Debounced lookup via GET /api/users/search?q=
function lookupRecipient() {
    clearTimeout(recipientLookupTimer);
    const q   = document.getElementById('send-recipient').value.trim();
    const pre = document.getElementById('recipient-preview');
    sendState.recipientId = null;
    sendState.recipientDisplay = null;
    if (!q) { pre.textContent = ''; return; }
    pre.textContent = 'Searching...';
    recipientLookupTimer = setTimeout(async () => {
        try {
            const res = await apiRequest(`/users/search?q=${encodeURIComponent(q)}`);
            const u   = res.data;
            sendState.recipientId      = u.id || u.accountNumber || q;
            sendState.recipientDisplay = `${u.fullName} (${u.accountNumber})`;
            pre.textContent = `✓ ${sendState.recipientDisplay}`;
            pre.style.color = 'var(--success)';
        } catch (err) {
            pre.textContent = `✗ ${err.message}`;
            pre.style.color = 'var(--danger)';
        }
    }, 550);
}

// Live risk preview using POST /api/transfers/precheck-risk
async function updateRisk() {
    const amount = parseFloat(document.getElementById('send-amount').value);
    if (!amount || !sendState.recipientId) return;

    try {
        const res = await apiRequest('/transfers/precheck-risk', 'POST', {
            recipientId: sendState.recipientId,
            amount,
            purpose: document.getElementById('send-purpose').value || ''
        });
        const r   = res.data;
        sendState.riskAssessment = r;
        document.getElementById('risk-badge').outerHTML = riskBadge(r.totalScore);
        document.getElementById('risk-reasons').innerHTML = (r.signals || [])
            .map(s => `• ${s.reason} (+${s.score})`).join('<br>') || '• No signals triggered';
    } catch { /* keep last */ }
}

function buildReview() {
    const amount  = parseFloat(document.getElementById('send-amount').value);
    const purpose = document.getElementById('send-purpose').value.trim() || '(no note)';
    sendState.amount  = amount;
    sendState.purpose = purpose;

    document.getElementById('review-recipient').textContent = sendState.recipientDisplay || sendState.recipientId;
    document.getElementById('review-amount').textContent    = fmtBDT(amount);
    document.getElementById('review-purpose').textContent   = purpose;

    // Show step-up if risk is HIGH
    const score   = sendState.riskAssessment?.totalScore || 0;
    const stepupEl = document.getElementById('stepup-box');
    if (score > 60) {
        stepupEl.style.display = 'flex';
    } else {
        stepupEl.style.display = 'none';
    }
}

// POST /api/transfers with Idempotency-Key header
async function executeSend() {
    // Step-up check
    const score    = sendState.riskAssessment?.totalScore || 0;
    const stepupEl = document.getElementById('stepup-box');
    if (stepupEl.style.display !== 'none') {
        const acked = document.getElementById('high-risk-confirm').checked;
        if (!acked) {
            showAlert('send-alert', 'Please acknowledge the Risk Shield step-up warning to proceed.');
            return;
        }
    }

    if (!sendState.recipientId) {
        showAlert('send-alert', 'Recipient not found or not resolved. Please go back and search again.');
        return;
    }

    hideAlert('send-alert');
    const btn = document.getElementById('btn-execute');
    btn.disabled = true; btn.textContent = 'Authorizing...';

    sendState.idempotencyKey = generateUUID();

    try {
        await apiRequest('/transfers', 'POST', {
            recipientId: sendState.recipientId,
            amount:      sendState.amount,
            purpose:     sendState.purpose
        }, { 'Idempotency-Key': sendState.idempotencyKey });

        document.getElementById('success-msg').textContent =
            `${fmtBDT(sendState.amount)} sent to ${sendState.recipientDisplay || sendState.recipientId}. Key: ${sendState.idempotencyKey.slice(0, 8)}...`;

        openModal('modal-success');
    } catch (err) {
        showAlert('send-alert', err.message);
    } finally {
        btn.disabled = false;
        btn.innerHTML = `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="20 6 9 17 4 12"/></svg> Authorize & Settle`;
    }
}

function newTransfer() {
    closeModal('modal-success');
    navigateTo('send');
}

/* ============================================================
   MONEY REQUESTS
   ============================================================ */
function setRequestTab(tab) {
    currentTab = tab;
    document.getElementById('tab-incoming').className = tab === 'incoming' ? 'btn btn-secondary btn-sm' : 'btn btn-outline btn-sm';
    document.getElementById('tab-outgoing').className = tab === 'outgoing' ? 'btn btn-secondary btn-sm' : 'btn btn-outline btn-sm';
    loadRequests();
}

async function loadRequests() {
    const el = document.getElementById('requests-list');
    el.innerHTML = `<div class="empty-state">Loading...</div>`;
    try {
        const res  = await apiRequest(`/requests/${currentTab}`);
        const list = res.data || [];
        if (!list.length) { el.innerHTML = `<div class="empty-state">No ${currentTab} requests.</div>`; return; }
        el.innerHTML = list.map(r => renderRequestRow(r)).join('');
    } catch (err) {
        el.innerHTML = `<div class="empty-state" style="color:var(--danger);">${err.message}</div>`;
    }
}

function renderRequestRow(r) {
    const isIncoming = currentTab === 'incoming';
    const canPay     = isIncoming && (r.status === 'PENDING' || r.status === 'PARTIALLY_PAID');
    const canCancel  = !isIncoming && r.status === 'PENDING';
    return `
    <div class="req-row">
        <div>
            <div class="font-bold" style="font-size:13px;">${isIncoming ? r.requesterUsername : r.payerUsername}</div>
            <div style="font-size:12px;color:var(--text-body);">${r.note || '—'}</div>
            <div class="font-xs text-muted">${fmtDate(r.createdAt)}</div>
        </div>
        <div style="text-align:right;">
            <div style="font-size:16px;font-weight:700;color:var(--primary);">${fmtBDT(r.amount)}</div>
            <div class="font-xs text-muted">Remaining: ${fmtBDT(r.remainingAmount)}</div>
        </div>
        <div class="req-row-actions">
            ${statusBadge(r.status)}
            ${canPay    ? `<button class="btn btn-primary btn-xs" onclick="openPayReqModal('${r.id}','${r.remainingAmount}','${r.requesterUsername}')">Pay</button>` : ''}
            ${canCancel ? `<button class="btn btn-danger btn-xs" onclick="cancelRequest('${r.id}')">Cancel</button>` : ''}
        </div>
    </div>`;
}

// Create request: POST /api/requests
async function submitCreateRequest(e) {
    e.preventDefault();
    const payer  = document.getElementById('req-payer').value.trim();
    const amount = parseFloat(document.getElementById('req-amount').value);
    const note   = document.getElementById('req-note').value.trim();

    // Lookup payer ID first
    let payerId;
    try {
        const lu = await apiRequest(`/users/search?q=${encodeURIComponent(payer)}`);
        payerId  = lu.data.id;
    } catch {
        alert('Payer not found. Please enter a valid username or account number.');
        return;
    }

    try {
        await apiRequest('/requests', 'POST', { payerId, amount, note });
        closeModal('modal-create-req');
        document.getElementById('req-payer').value  = '';
        document.getElementById('req-amount').value = '';
        document.getElementById('req-note').value   = '';
        loadRequests();
    } catch (err) {
        alert(err.message);
    }
}

// Pay request: POST /api/requests/{id}/partial-pay or /api/requests/{id}/accept
function openPayReqModal(id, maxAmount, requesterName) {
    payReqState = { id, maxAmount: parseFloat(maxAmount) };
    document.getElementById('pay-req-id').value = id;
    document.getElementById('pay-req-amount').value = parseFloat(maxAmount).toFixed(2);
    document.getElementById('pay-req-amount').max   = parseFloat(maxAmount);
    document.getElementById('pay-req-details').innerHTML = `
        <div class="review-row"><span class="review-key">Requester</span><span class="review-val">${requesterName}</span></div>
        <div class="review-row"><span class="review-key">Outstanding</span><span class="review-val">${fmtBDT(maxAmount)}</span></div>`;
    openModal('modal-pay-req');
}

async function submitPayRequest(e) {
    e.preventDefault();
    const id     = document.getElementById('pay-req-id').value;
    const amount = parseFloat(document.getElementById('pay-req-amount').value);
    const idKey  = generateUUID();
    const isPartial = amount < payReqState.maxAmount;
    const endpoint  = isPartial ? `/requests/${id}/partial-pay` : `/requests/${id}/accept`;

    try {
        await apiRequest(endpoint, 'POST', { amount, idempotencyKey: idKey }, { 'Idempotency-Key': idKey });
        closeModal('modal-pay-req');
        loadRequests();
        loadDashboard();
    } catch (err) {
        alert(err.message);
    }
}

async function cancelRequest(id) {
    if (!confirm('Cancel this money request?')) return;
    try {
        await apiRequest(`/requests/${id}/cancel`, 'POST');
        loadRequests();
    } catch (err) { alert(err.message); }
}

/* ============================================================
   GROUP COLLECT
   ============================================================ */
async function loadGroups() {
    const grid = document.getElementById('groups-grid');
    grid.innerHTML = `<div class="empty-state">Loading collections...</div>`;
    try {
        const res  = await apiRequest('/groups');
        const list = res.data || [];
        if (!list.length) { grid.innerHTML = `<div class="empty-state">No collections yet. Create one! 👥</div>`; return; }
        grid.innerHTML = list.map(renderGroupCard).join('');
    } catch (err) {
        grid.innerHTML = `<div class="empty-state" style="color:var(--danger);">${err.message}</div>`;
    }
}

function renderGroupCard(g) {
    const pct     = g.targetAmount > 0 ? Math.min(100, (g.collectedAmount / g.targetAmount) * 100) : 0;
    const members = (g.members || []).length;
    return `
    <div class="card">
        <div class="flex-between mb-8">
            <div class="card-title" style="font-size:16px;">${g.title}</div>
            ${statusBadge(g.status)}
        </div>
        <div class="font-sm text-body mb-16">${g.description || ''}</div>
        <div class="flex-between mb-4">
            <span class="font-xs text-muted">Collected</span>
            <span class="font-bold text-primary">${fmtBDT(g.collectedAmount)} / ${fmtBDT(g.targetAmount)}</span>
        </div>
        <div class="progress-bar-outer"><div class="progress-bar-inner" style="width:${pct.toFixed(1)}%;"></div></div>
        <div class="font-xs text-muted mt-4 mb-16">${pct.toFixed(0)}% funded · ${members} member${members !== 1 ? 's' : ''}</div>
        <div style="display:flex;gap:8px;flex-wrap:wrap;">
            ${g.status === 'ACTIVE' ? `<button class="btn btn-primary btn-sm" onclick="openPayGroupModal('${g.id}')">Contribute</button>` : ''}
            ${g.status === 'ACTIVE' ? `<button class="btn btn-outline btn-sm" onclick="cancelGroup('${g.id}')">Cancel</button>` : ''}
        </div>
    </div>`;
}

async function submitCreateGroup(e) {
    e.preventDefault();
    const memberNames = document.getElementById('grp-members').value.split(',').map(s => s.trim()).filter(Boolean);
    let   memberIds   = [];

    for (const name of memberNames) {
        try {
            const lu = await apiRequest(`/users/search?q=${encodeURIComponent(name)}`);
            memberIds.push({ userId: lu.data.id, targetContribution: null });
        } catch { /* skip unresolved */ }
    }

    try {
        await apiRequest('/groups', 'POST', {
            title:        document.getElementById('grp-title').value.trim(),
            targetAmount: parseFloat(document.getElementById('grp-target').value),
            members:      memberIds
        });
        closeModal('modal-create-group');
        document.getElementById('grp-title').value   = '';
        document.getElementById('grp-target').value  = '';
        document.getElementById('grp-members').value = '';
        loadGroups();
    } catch (err) { alert(err.message); }
}

function openPayGroupModal(groupId) {
    const amount = parseFloat(prompt('Enter contribution amount (BDT):') || '0');
    if (!amount || amount <= 0) return;
    const idKey = generateUUID();
    apiRequest(`/groups/${groupId}/pay`, 'POST', {
        amount, idempotencyKey: idKey
    }, { 'Idempotency-Key': idKey })
        .then(() => { alert('Contribution submitted!'); loadGroups(); loadDashboard(); })
        .catch(err => alert(err.message));
}

async function cancelGroup(id) {
    if (!confirm('Cancel this collection?')) return;
    try {
        await apiRequest(`/groups/${id}/cancel`, 'POST');
        loadGroups();
    } catch (err) { alert(err.message); }
}

/* ============================================================
   ACTIVITY (transaction history)
   ============================================================ */
async function loadActivity(page = 1) {
    currentPage = page;
    const tbody = document.getElementById('txn-tbody');
    tbody.innerHTML = `<tr><td colspan="7" class="empty-state">Loading...</td></tr>`;

    document.getElementById('txn-page-label').textContent = `Page ${page}`;
    document.getElementById('btn-prev').disabled = page <= 1;

    try {
        const res  = await apiRequest(`/transfers?page=${page}&pageSize=15`);
        const list = res.data || [];

        if (!list.length) {
            tbody.innerHTML = `<tr><td colspan="7" class="empty-state">No transactions on page ${page}.</td></tr>`;
            document.getElementById('btn-next').disabled = true;
            return;
        }

        document.getElementById('btn-next').disabled = list.length < 15;

        tbody.innerHTML = list.map(t => {
            const isOut = t.senderUserId === currentUser?.id;
            return `
            <tr>
                <td><span class="mono-id">${t.transactionNumber || t.id?.slice(0, 12) + '...'}</span></td>
                <td class="font-sm">${fmtDate(t.createdAt)}</td>
                <td class="font-bold">${isOut ? t.recipientUsername : t.senderUsername}</td>
                <td class="font-sm text-body">${t.purpose || '—'}</td>
                <td>${statusBadge(t.status)}</td>
                <td class="${isOut ? 'amt-out' : 'amt-in'}">${isOut ? '−' : '+'}${fmtBDT(t.amount)}</td>
                <td><button class="btn btn-ghost btn-xs" onclick="viewTxnDetail('${t.id}')">Details →</button></td>
            </tr>`;
        }).join('');
    } catch (err) {
        tbody.innerHTML = `<tr><td colspan="7" class="empty-state" style="color:var(--danger);">${err.message}</td></tr>`;
    }
}

function changePage(delta) {
    loadActivity(Math.max(1, currentPage + delta));
}

async function viewTxnDetail(id) {
    openModal('modal-txn');
    document.getElementById('modal-txn-body').innerHTML = `<div class="empty-state">Loading receipt...</div>`;
    try {
        const res = await apiRequest(`/transfers/${id}`);
        const t   = res.data;
        const isOut = t.senderUserId === currentUser?.id;

        document.getElementById('modal-txn-body').innerHTML = `
            <div class="review-box mb-16">
                <div class="review-row"><span class="review-key">Transaction #</span><span class="mono-id">${t.transactionNumber || '—'}</span></div>
                <div class="review-row"><span class="review-key">Status</span><span>${statusBadge(t.status)}</span></div>
                <div class="review-row"><span class="review-key">Date</span><span class="review-val">${fmtDate(t.createdAt)}</span></div>
                <div class="review-row"><span class="review-key">Sender</span><span class="review-val">${t.senderUsername}</span></div>
                <div class="review-row"><span class="review-key">Recipient</span><span class="review-val">${t.recipientUsername}</span></div>
                <div class="review-row"><span class="review-key">Amount</span><span class="review-val ${isOut ? 'amt-out' : 'amt-in'}">${isOut ? '−' : '+'}${fmtBDT(t.amount)}</span></div>
                <div class="review-row"><span class="review-key">Purpose</span><span class="review-val">${t.purpose || '—'}</span></div>
                <div class="review-row"><span class="review-key">Idempotency Key</span><span class="mono-id">${t.idempotencyKey || '—'}</span></div>
            </div>

            ${t.timeline?.length ? `
            <div class="mb-16">
                <div class="font-bold mb-8" style="font-size:14px;color:var(--primary);">Immutable Event Timeline</div>
                <div style="border-left:3px solid var(--primary);padding-left:14px;">
                    ${t.timeline.map((ev, i) => `
                    <div style="margin-bottom:10px;position:relative;">
                        <div style="position:absolute;left:-19px;top:3px;width:11px;height:11px;border-radius:50%;background:${i === 0 ? 'var(--success)' : 'var(--border)'};border:2px solid #fff;"></div>
                        <div class="font-bold font-sm">${ev.event || ev.eventType || ev.type}</div>
                        <div class="font-xs text-muted">${fmtDate(ev.timestamp || ev.createdAt)}</div>
                        ${ev.note ? `<div class="font-xs text-body">${ev.note}</div>` : ''}
                    </div>`).join('')}
                </div>
            </div>` : ''}

            ${t.riskSignals?.length ? `
            <div>
                <div class="font-bold mb-8" style="font-size:14px;color:var(--primary);">Risk Shield Analysis ${riskBadge(t.riskSignals.reduce((s, r) => s + r.score, 0))}</div>
                ${t.riskSignals.map(s => `
                <div class="risk-box mb-8">
                    <div class="flex-between"><span class="font-bold font-sm">${s.reason}</span><span class="badge badge-warning">+${s.score}</span></div>
                </div>`).join('')}
            </div>` : ''}`;
    } catch (err) {
        document.getElementById('modal-txn-body').innerHTML = `<div class="empty-state" style="color:var(--danger);">${err.message}</div>`;
    }
}

/* ============================================================
   RECOVERY CENTER
   ============================================================ */
async function loadRecovery() {
    const el = document.getElementById('recovery-list');
    el.innerHTML = `<div class="empty-state">Loading cases...</div>`;
    try {
        const res  = await apiRequest('/recovery');
        const list = res.data || [];
        if (!list.length) { el.innerHTML = `<div class="empty-state">No recovery cases. ✓ Everything looks healthy.</div>`; return; }
        el.innerHTML = list.map(c => `
            <div class="req-row">
                <div>
                    <div class="font-bold">${c.issueType || c.issueTypeDisplay || '—'}</div>
                    <div class="font-xs text-muted">${c.description || ''}</div>
                    <div class="font-xs text-muted">${fmtDate(c.createdAt)}</div>
                    ${c.txnId ? `<div class="mono-id mt-4">TXN: ${c.txnId}</div>` : ''}
                </div>
                <div class="req-row-actions">
                    ${statusBadge(c.status)}
                    ${c.status === 'OPEN' || c.status === 'UNDER_INVESTIGATION' ? `
                    <button class="btn btn-secondary btn-xs" onclick="investigateCase('${c.id}')">Investigate</button>` : ''}
                </div>
            </div>`).join('');
    } catch (err) {
        el.innerHTML = `<div class="empty-state" style="color:var(--danger);">${err.message}</div>`;
    }
}

async function submitRecoveryCase(e) {
    e.preventDefault();
    try {
        await apiRequest('/recovery', 'POST', {
            transactionId: document.getElementById('rec-txn-id').value.trim(),
            issueType:     document.getElementById('rec-issue').value,
            description:   document.getElementById('rec-desc').value.trim()
        });
        closeModal('modal-file-recovery');
        document.getElementById('rec-txn-id').value = '';
        document.getElementById('rec-desc').value   = '';
        loadRecovery();
    } catch (err) { alert(err.message); }
}

async function investigateCase(id) {
    try {
        await apiRequest(`/recovery/${id}/investigate`, 'POST');
        loadRecovery();
    } catch (err) { alert(err.message); }
}

/* ============================================================
   PROFILE
   ============================================================ */
async function loadProfile() {
    const el = document.getElementById('profile-body');
    el.innerHTML = `<div class="empty-state">Loading profile...</div>`;
    try {
        const [walletRes, profileRes] = await Promise.all([
            apiRequest('/wallet'),
            apiRequest('/users/profile')
        ]);
        const w = walletRes.data;
        const p = profileRes.data || currentUser;
        el.innerHTML = `
            <div style="text-align:center;padding-bottom:20px;border-bottom:1px solid var(--border-subtle);margin-bottom:20px;">
                <div class="profile-pix-top-banner" style="width:68px;height:68px;font-size:28px;margin:0 auto 10px;">
                    ${(p.fullName || p.username || 'U')[0].toUpperCase()}
                </div>
                <div style="font-size:20px;font-weight:700;color:var(--primary);">${p.fullName || p.username}</div>
                <div class="font-sm text-muted">@${p.username} • ${p.email || ''}</div>
            </div>
            <div class="review-box">
                <div class="review-row"><span class="review-key">Account Number</span><span class="mono-id">${p.accountNumber || w.accountNumber || '—'}</span></div>
                <div class="review-row"><span class="review-key">Balance</span><span class="review-val" style="color:var(--primary);font-size:18px;font-weight:700;">${fmtBDT(w.balance)}</span></div>
                <div class="review-row"><span class="review-key">Total Sent</span><span class="amt-out">${fmtBDT(w.totalDebitedAmount || 0)}</span></div>
                <div class="review-row"><span class="review-key">Total Received</span><span class="amt-in">${fmtBDT(w.totalCreditedAmount || 0)}</span></div>
                <div class="review-row"><span class="review-key">Protection Status</span><span class="badge badge-success">🛡️ Zero-Variance Ledger Active</span></div>
            </div>
            <div class="flex-end"><button class="btn btn-danger" onclick="handleLogout()">Sign Out</button></div>`;
    } catch (err) {
        el.innerHTML = `<div class="empty-state" style="color:var(--danger);">${err.message}</div>`;
    }
}

/* ============================================================
   TRUST LAB
   ============================================================ */
async function runAllTests() {
    await Promise.allSettled([runDup(), runCon(), runRet(), runAud()]);
}

async function runDup() {
    setLabState('dup', 'Running...');
    try {
        const res = await apiRequest('/trust-lab/duplicate-test', 'POST');
        const d   = res.data;
        document.getElementById('res-dup').innerHTML =
            `Attempts: ${d.totalAttempts} | Debits: ${d.uniqueDebits ?? d.successCount ?? '—'} | Blocked: ${d.blockedDuplicates ?? '—'}<br>
             <strong>${d.passed ? '✓ PASSED' : '✗ FAILED'}: ${d.summary || ''}</strong>`;
        setLabState('dup', d.passed ? 'PASS' : 'FAIL', d.passed ? 'badge-success' : 'badge-danger');
    } catch (err) { setLabState('dup', 'Error', 'badge-danger'); document.getElementById('res-dup').textContent = err.message; }
}

async function runCon() {
    setLabState('con', 'Running...');
    try {
        const res = await apiRequest('/trust-lab/concurrency-test', 'POST');
        const d   = res.data;
        document.getElementById('res-con').innerHTML =
            `Success: ${d.successCount} | Failed: ${d.failureCount} | Overdraft: ${d.overdraftOccurred ? '⚠ YES' : '✓ NO'}<br>
             Final Balance: ${fmtBDT(d.finalBalance)}<br>
             <strong>${d.passed ? '✓ PASSED' : '✗ FAILED'}: ${d.summary || ''}</strong>`;
        setLabState('con', d.passed ? 'PASS' : 'FAIL', d.passed ? 'badge-success' : 'badge-danger');
    } catch (err) { setLabState('con', 'Error', 'badge-danger'); document.getElementById('res-con').textContent = err.message; }
}

async function runRet() {
    setLabState('ret', 'Running...');
    try {
        const res = await apiRequest('/trust-lab/retry-test', 'POST');
        const d   = res.data;
        document.getElementById('res-ret').innerHTML =
            `Retries: ${d.retryCount} | Double Debit: ${d.doubleDebitOccurred ? '⚠ YES' : '✓ NO'}<br>
             <strong>${d.passed ? '✓ PASSED' : '✗ FAILED'}: ${d.summary || ''}</strong>`;
        setLabState('ret', d.passed ? 'PASS' : 'FAIL', d.passed ? 'badge-success' : 'badge-danger');
    } catch (err) { setLabState('ret', 'Error', 'badge-danger'); document.getElementById('res-ret').textContent = err.message; }
}

async function runAud() {
    setLabState('aud', 'Running...');
    try {
        const res = await apiRequest('/trust-lab/ledger-integrity');
        const d   = res.data;
        document.getElementById('res-aud').innerHTML =
            `Total Debits: ${fmtBDT(d.totalDebits)} | Total Credits: ${fmtBDT(d.totalCredits)}<br>
             Variance: ${fmtBDT(d.variance ?? 0)} | Entries: ${d.entryCount ?? '—'}<br>
             <strong>${d.isBalanced ? '✓ ZERO VARIANCE — LEDGER BALANCED' : `✗ VARIANCE DETECTED: ${d.variance}`}</strong>`;
        setLabState('aud', d.isBalanced ? 'PASS' : 'FAIL', d.isBalanced ? 'badge-success' : 'badge-danger');
    } catch (err) { setLabState('aud', 'Error', 'badge-danger'); document.getElementById('res-aud').textContent = err.message; }
}

function setLabState(key, label, cls = 'badge-neutral') {
    const el = document.getElementById(`st-${key}`);
    if (!el) return;
    el.className = `badge ${cls}`;
    el.textContent = label;
}

/* ============================================================
   MODALS
   ============================================================ */
function openModal(id)  { const m = document.getElementById(id); if (m) m.style.display = 'flex'; }
function closeModal(id) { const m = document.getElementById(id); if (m) m.style.display = 'none'; }

function backdropClose(e, id) {
    if (e.target === e.currentTarget) closeModal(id);
}
