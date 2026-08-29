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
    if (!st) return '<span class="badge badge-neutral">—</span>';
    const norm = String(st).toUpperCase().replace(/\s+/g, '_');
    const m = {
        'COMPLETED':            ['badge-success', '✓ Completed'],
        'SUCCEEDED':            ['badge-success', '✓ Succeeded'],
        'COMPLETED_IDEMPOTENT': ['badge-success', '♻ Deduplicated'],
        'PROCESSING':           ['badge-warning', '⏳ Processing'],
        'FAILED':               ['badge-danger',  '✗ Failed'],
        'ROLLED_BACK':          ['badge-danger',  '↩ Rolled Back'],
        'UNKNOWN':              ['badge-neutral', '? Unknown'],
        'PENDING':              ['badge-warning', '⏳ Pending'],
        'PARTIALLYPAID':        ['badge-warning', '≈ Partial'],
        'PARTIALLY_PAID':       ['badge-warning', '≈ Partial'],
        'PAID':                 ['badge-success', '✓ Paid'],
        'REJECTED':             ['badge-danger',  '✗ Rejected'],
        'CANCELLED':            ['badge-neutral', '✗ Cancelled'],
        'ACTIVE':               ['badge-info',    '● Active'],
        'OPEN':                 ['badge-info',    '● Open'],
        'UNDER_INVESTIGATION':  ['badge-warning', '🔍 Investigating'],
        'RESOLVED':             ['badge-success', '✓ Resolved'],
        'CLOSED':               ['badge-neutral', 'Closed'],
    };
    const [cls, label] = m[norm] || ['badge-neutral', st];
    return `<span class="badge ${cls}">${label}</span>`;
}

function riskBadge(score) {
    if (score <= 30)  return `<span class="badge badge-success">Score: ${score} (LOW)</span>`;
    if (score <= 60)  return `<span class="badge badge-warning">Score: ${score} (MEDIUM)</span>`;
    return `<span class="badge badge-danger">Score: ${score} (HIGH)</span>`;
}

function getTransactionMeta(t) {
    const myUser = (currentUser?.username || '').toLowerCase();
    const myAcc  = (currentUser?.accountNumber || '').toUpperCase();

    const senderUser   = (t.senderUsername || '').toLowerCase();
    const senderAcc    = (t.senderAccountNumber || '').toUpperCase();
    const receiverUser = (t.receiverUsername || t.recipientUsername || '').toLowerCase();
    const receiverAcc  = (t.receiverAccountNumber || t.recipientAccountNumber || '').toUpperCase();

    const isDebit = (senderUser && senderUser === myUser) || (senderAcc && senderAcc === myAcc);
    const counterparty = isDebit
        ? (t.receiverUsername || t.recipientUsername || t.receiverAccountNumber || 'Recipient')
        : (t.senderUsername || t.senderAccountNumber || 'System Vault');

    const dateVal = t.createdAtUtc || t.createdAt;

    return {
        isDebit,
        counterparty,
        dateVal,
        amountSign: isDebit ? '− ' : '+ ',
        amountClass: isDebit ? 'amt-out' : 'amt-in'
    };
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
            let errorMsg = data.message || data.detail;
            if (!errorMsg && data.errors) {
                errorMsg = Object.values(data.errors).flat().join(' ');
            }
            throw new Error(errorMsg || data.title || 'Request failed.');
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
    document.addEventListener('click', (e) => {
        const notifDropdown = document.getElementById('notif-dropdown');
        const notifBtn = document.getElementById('btn-notif');
        if (notifDropdown && notifDropdown.classList.contains('show')) {
            if (!notifDropdown.contains(e.target) && !notifBtn.contains(e.target)) {
                notifDropdown.classList.remove('show');
            }
        }
    });

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
        loadNotifications();
        if (!window.notifInterval) {
            window.notifInterval = setInterval(loadNotifications, 12000);
        }
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
        if (currentUser.balance !== undefined && currentUser.balance !== null) {
            const balEl = document.getElementById('dash-balance');
            if (balEl) balEl.textContent = fmtBDT(currentUser.balance);
        }
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
            accountNumber: res.data.accountNumber,
            balance:       res.data.balance ?? res.data.availableBalance ?? 0
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
            accountNumber: res.data.accountNumber,
            balance:       res.data.balance ?? res.data.availableBalance ?? 100000
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
        const balance = wallet.data.availableBalance ?? wallet.data.balance ?? currentUser?.balance ?? 0;
        document.getElementById('dash-balance').textContent = fmtBDT(balance);
        if (currentUser) {
            currentUser.balance = balance;
        }
    } catch (err) {
        console.error('Error loading wallet balance:', err);
        if (currentUser?.balance !== undefined) {
            document.getElementById('dash-balance').textContent = fmtBDT(currentUser.balance);
        }
    }

    // Pending incoming requests
    try {
        const reqs = await apiRequest('/requests/incoming');
        const list  = (reqs.data || []).filter(r => {
            const st = (r.status || '').toUpperCase();
            return st === 'PENDING' || st === 'PARTIALLYPAID' || st === 'PARTIALLY_PAID';
        }).slice(0, 4);
        const el    = document.getElementById('dash-pending');
        el.innerHTML = list.length ? list.map(r => {
            const reqName = r.requesterName || r.requesterUsername || r.requesterAccountNumber || 'Peer';
            return `
            <div class="req-row">
                <div>
                    <div class="font-bold" style="font-size:13px;">${reqName} <span class="text-muted font-sm">asks</span></div>
                    <div style="font-size:13px;font-weight:700;color:var(--primary);">${fmtBDT(r.remainingAmount)}</div>
                    <div class="font-xs text-muted">${r.note || ''}</div>
                </div>
                <div class="req-row-actions">
                    ${statusBadge(r.status)}
                    <button class="btn btn-primary btn-xs" onclick="openPayReqModal('${r.id}','${r.remainingAmount}','${reqName}')">Pay</button>
                </div>
            </div>`;
        }).join('')
            : `<div class="empty-state">No pending requests 🎉</div>`;
    } catch { document.getElementById('dash-pending').innerHTML = `<div class="empty-state">Could not load requests.</div>`; }

    // Recent transactions
    try {
        const txns = await apiRequest('/transfers?page=1&pageSize=4');
        const el   = document.getElementById('dash-recent');
        const list = txns.data || [];
        el.innerHTML = list.length ? list.map(t => {
            const meta = getTransactionMeta(t);
            return `
            <div class="req-row">
                <div>
                    <div class="font-bold" style="font-size:13px;">${meta.counterparty} • <span class="text-muted font-sm">${t.purpose || 'Direct safe send'}</span></div>
                    <div class="font-xs text-muted">${fmtDate(meta.dateVal)}</div>
                </div>
                <div class="${meta.amountClass}">${meta.amountSign}${fmtBDT(t.amount)}</div>
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
    sendState.recipientId            = accOrUsername;
    sendState.recipientAccountNumber = accOrUsername;
    sendState.recipientDisplay       = displayText;
    document.getElementById('recipient-preview').textContent = `✓ ${displayText}`;
    clearTimeout(recipientLookupTimer);
}

// Debounced lookup via GET /api/users/search?q=
function lookupRecipient() {
    clearTimeout(recipientLookupTimer);
    const q   = document.getElementById('send-recipient').value.trim();
    const pre = document.getElementById('recipient-preview');
    sendState.recipientId = null;
    sendState.recipientAccountNumber = null;
    sendState.recipientDisplay = null;
    if (!q) { pre.textContent = ''; return; }
    pre.textContent = 'Searching...';
    recipientLookupTimer = setTimeout(async () => {
        try {
            const res = await apiRequest(`/users/search?q=${encodeURIComponent(q)}`);
            const u   = res.data;
            sendState.recipientId            = u.accountNumber || u.id || q;
            sendState.recipientAccountNumber = u.accountNumber || q;
            sendState.recipientDisplay       = `${u.fullName} (${u.accountNumber})`;
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
            recipientId:           sendState.recipientAccountNumber || sendState.recipientId,
            receiverAccountNumber: sendState.recipientAccountNumber || sendState.recipientId,
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
    let confirmHighRisk = false;
    if (stepupEl.style.display !== 'none') {
        const acked = document.getElementById('high-risk-confirm').checked;
        if (!acked) {
            showAlert('send-alert', 'Please acknowledge the Risk Shield step-up warning to proceed.');
            return;
        }
        confirmHighRisk = true;
    }

    const recipientTarget = sendState.recipientAccountNumber || sendState.recipientId;
    if (!recipientTarget) {
        showAlert('send-alert', 'Recipient not found or not resolved. Please go back and search again.');
        return;
    }

    hideAlert('send-alert');
    const btn = document.getElementById('btn-execute');
    btn.disabled = true; btn.textContent = 'Authorizing...';

    sendState.idempotencyKey = generateUUID();

    try {
        await apiRequest('/transfers', 'POST', {
            recipientId:           recipientTarget,
            receiverAccountNumber: recipientTarget,
            amount:                sendState.amount,
            purpose:               sendState.purpose,
            confirmHighRisk:       confirmHighRisk
        }, { 'Idempotency-Key': sendState.idempotencyKey });

        document.getElementById('success-msg').textContent =
            `${fmtBDT(sendState.amount)} sent to ${sendState.recipientDisplay || recipientTarget}. Key: ${sendState.idempotencyKey.slice(0, 8)}...`;

        showToast('Transfer Succeeded', `Sent ${fmtBDT(sendState.amount)} to ${sendState.recipientDisplay || recipientTarget}`, 'success');
        openModal('modal-success');
        loadNotifications();
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
    const isIncoming  = currentTab === 'incoming';
    const statusUpper = (r.status || '').toUpperCase().replace(/\s+/g, '_');
    const canPay      = isIncoming && (statusUpper === 'PENDING' || statusUpper === 'PARTIALLYPAID' || statusUpper === 'PARTIALLY_PAID');
    const canCancel   = !isIncoming && statusUpper === 'PENDING';
    const displayName = isIncoming 
        ? (r.requesterName || r.requesterUsername || r.requesterAccountNumber || 'Peer Requester')
        : (r.payerName || r.payerUsername || r.payerAccountNumber || 'Peer Payer');
    const dateVal = r.createdAtUtc || r.createdAt;

    return `
    <div class="req-row">
        <div>
            <div class="font-bold" style="font-size:14px;color:var(--primary);">${displayName}</div>
            <div style="font-size:12px;color:var(--text-body);margin-top:2px;">${r.note || '—'}</div>
            <div class="font-xs text-muted" style="margin-top:3px;">${fmtDate(dateVal)}</div>
        </div>
        <div style="text-align:right;">
            <div style="font-size:16px;font-weight:700;color:var(--primary);">${fmtBDT(r.amount)}</div>
            <div class="font-xs text-muted">Remaining: ${fmtBDT(r.remainingAmount)}</div>
        </div>
        <div class="req-row-actions">
            ${statusBadge(r.status)}
            ${canPay    ? `<button class="btn btn-primary btn-sm" onclick="openPayReqModal('${r.id}','${r.remainingAmount}','${displayName}')">Pay Now</button>` : ''}
            ${canCancel ? `<button class="btn btn-danger btn-sm" onclick="cancelRequest('${r.id}')">Cancel</button>` : ''}
        </div>
    </div>`;
}

// Create request: POST /api/requests
async function submitCreateRequest(e) {
    e.preventDefault();
    const payerInput = document.getElementById('req-payer').value.trim();
    const amount     = parseFloat(document.getElementById('req-amount').value);
    const note       = document.getElementById('req-note').value.trim();

    if (!payerInput) {
        alert('Please enter a payer username or account number.');
        return;
    }
    if (!amount || amount <= 0) {
        alert('Please enter a valid amount.');
        return;
    }

    // Lookup payer identifier
    let payerAccNum = payerInput;
    try {
        const lu = await apiRequest(`/users/search?q=${encodeURIComponent(payerInput)}`);
        if (lu.data) {
            payerAccNum = lu.data.accountNumber || lu.data.id || payerInput;
        }
    } catch {
        // Fallback to direct text value
    }

    try {
        await apiRequest('/requests', 'POST', {
            payerAccountNumber: payerAccNum,
            payerId:            payerAccNum,
            amount:             amount,
            note:               note
        });
        closeModal('modal-create-req');
        document.getElementById('req-payer').value  = '';
        document.getElementById('req-amount').value = '';
        document.getElementById('req-note').value   = '';
        showToast('Request Created', `Requested ${fmtBDT(amount)} from ${payerInput}`, 'info');
        loadRequests();
        loadNotifications();
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
        await apiRequest(endpoint, 'POST', {
            amount:         amount,
            paymentAmount:  amount,
            idempotencyKey: idKey
        }, { 'Idempotency-Key': idKey });
        closeModal('modal-pay-req');
        showToast('Payment Complete', `Paid ${fmtBDT(amount)} on money request`, 'success');
        loadRequests();
        loadDashboard();
        loadNotifications();
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
    const pct         = g.targetAmount > 0 ? Math.min(100, (g.collectedAmount / g.targetAmount) * 100) : 0;
    const members     = g.members || [];
    const isCreator   = g.creatorUserId === currentUser?.id;
    const myMember    = members.find(m => m.userId === currentUser?.id);
    const statusUpper = (g.status || '').toUpperCase();
    const isActive    = statusUpper === 'PENDING' || statusUpper === 'PARTIALLYPAID' || statusUpper === 'PARTIALLY_PAID' || statusUpper === 'ACTIVE';

    const myRemaining = myMember ? myMember.remainingAmount : 0;
    const canContribute = isActive && (myMember ? ((myMember.status || '').toUpperCase() !== 'PAID') : (!isCreator));
    const canCancel   = isCreator && isActive;

    const membersHtml = members.length > 0 ? `
        <div style="margin-top:14px;padding-top:12px;border-top:1px solid var(--border-subtle);">
            <div class="font-xs font-bold text-muted mb-8" style="text-transform:uppercase;letter-spacing:.5px;">Member Breakdown (${members.length})</div>
            <div style="display:flex;flex-direction:column;gap:6px;">
                ${members.map(m => {
                    const isMe = m.userId === currentUser?.id;
                    return `
                    <div style="display:flex;align-items:center;justify-content:space-between;font-size:12px;">
                        <span style="color:var(--text-heading);font-weight:${isMe ? '700' : '500'};">
                            ${m.fullName || m.username || m.accountNumber} ${isMe ? '<span class="badge badge-info" style="padding:1px 6px;font-size:10px;">You</span>' : ''}
                        </span>
                        <div style="display:flex;align-items:center;gap:8px;">
                            <span style="color:var(--text-body);">${fmtBDT(m.paidAmount)} / ${fmtBDT(m.requiredAmount)}</span>
                            ${statusBadge(m.status)}
                        </div>
                    </div>`;
                }).join('')}
            </div>
        </div>` : '';

    return `
    <div class="card" style="display:flex;flex-direction:column;justify-content:space-between;">
        <div>
            <div class="flex-between mb-8">
                <div>
                    <div class="card-title" style="font-size:16px;">${g.title}</div>
                    <div class="font-xs text-muted">Created by ${isCreator ? 'You' : (g.creatorUsername || 'Peer')} • ${fmtDate(g.createdAtUtc || g.createdAt)}</div>
                </div>
                ${statusBadge(g.status)}
            </div>
            <div class="font-sm text-body mb-16">${g.description || ''}</div>
            <div class="flex-between mb-4">
                <span class="font-xs text-muted">Progress</span>
                <span class="font-bold text-primary">${fmtBDT(g.collectedAmount)} / ${fmtBDT(g.targetAmount)}</span>
            </div>
            <div class="progress-bar-outer"><div class="progress-bar-inner" style="width:${pct.toFixed(1)}%;"></div></div>
            <div class="flex-between font-xs text-muted mt-4 mb-16">
                <span>${pct.toFixed(0)}% funded</span>
                <span>Remaining: ${fmtBDT(g.remainingAmount)}</span>
            </div>
            ${membersHtml}
        </div>

        <div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:16px;padding-top:12px;border-top:1px solid var(--border-subtle);">
            ${canContribute ? `
                <button class="btn btn-primary btn-sm" onclick="openPayGroupModal('${g.id}', ${myRemaining || 100}, '${g.title.replace(/'/g, "\\'")}')">
                    ${myMember ? `Pay My Share (${fmtBDT(myRemaining)})` : 'Contribute Funds'}
                </button>` : ''}
            ${canCancel ? `
                <button class="btn btn-danger btn-sm" onclick="cancelGroup('${g.id}')">
                    Cancel Collection
                </button>` : ''}
        </div>
    </div>`;
}

function openPayGroupModal(groupId, suggestedAmount, groupTitle) {
    document.getElementById('pay-grp-id').value = groupId;
    const amountVal = (parseFloat(suggestedAmount) > 0 ? parseFloat(suggestedAmount) : 100).toFixed(2);
    document.getElementById('pay-grp-amount').value = amountVal;
    document.getElementById('pay-grp-details').innerHTML = `
        <div class="review-row"><span class="review-key">Collection</span><span class="review-val">${groupTitle || 'Group Collection'}</span></div>
        <div class="review-row"><span class="review-key">Assigned Contribution</span><span class="review-val">${fmtBDT(suggestedAmount || 100)}</span></div>`;
    openModal('modal-pay-group');
}

async function submitPayGroup(e) {
    e.preventDefault();
    const groupId = document.getElementById('pay-grp-id').value;
    const amount  = parseFloat(document.getElementById('pay-grp-amount').value);
    if (!amount || amount <= 0) {
        alert('Please enter a valid amount.');
        return;
    }

    const idKey = generateUUID();
    try {
        await apiRequest(`/groups/${groupId}/pay`, 'POST', {
            amount:         amount,
            idempotencyKey: idKey
        }, { 'Idempotency-Key': idKey });

        closeModal('modal-pay-group');
        showToast('Contribution Successful', `Contributed ${fmtBDT(amount)} to group pool`, 'success');
        loadGroups();
        loadDashboard();
        loadNotifications();
    } catch (err) {
        alert(err.message);
    }
}

async function submitCreateGroup(e) {
    e.preventDefault();
    const title       = document.getElementById('grp-title').value.trim();
    const descInput   = document.getElementById('grp-desc') ? document.getElementById('grp-desc').value.trim() : '';
    const description = descInput || title;
    const targetAmount= parseFloat(document.getElementById('grp-target').value);
    const memberNames = document.getElementById('grp-members').value.split(',').map(s => s.trim()).filter(Boolean);

    if (!title) {
        alert('Please enter a collection title.');
        return;
    }
    if (!targetAmount || targetAmount <= 0) {
        alert('Please enter a valid target amount.');
        return;
    }

    const members = [];
    for (const name of memberNames) {
        let accNum = name;
        try {
            const lu = await apiRequest(`/users/search?q=${encodeURIComponent(name)}`);
            if (lu.data) {
                accNum = lu.data.accountNumber || lu.data.username || name;
            }
        } catch { /* proceed with raw text */ }

        members.push({
            memberAccountNumber: accNum,
            memberId:            accNum,
            requiredAmount:      0
        });
    }

    try {
        await apiRequest('/groups', 'POST', {
            title:          title,
            description:    description,
            targetAmount:   targetAmount,
            initialMembers: members,
            members:        members
        });
        closeModal('modal-create-group');
        document.getElementById('grp-title').value   = '';
        if (document.getElementById('grp-desc')) document.getElementById('grp-desc').value = '';
        document.getElementById('grp-target').value  = '';
        document.getElementById('grp-members').value = '';
        showToast('Collection Launched', `Group Pool "${title}" created for ${fmtBDT(targetAmount)}`, 'success');
        loadGroups();
        loadNotifications();
    } catch (err) { alert(err.message); }
}

function openPayGroupModal(groupId) {
    const amount = parseFloat(prompt('Enter contribution amount (BDT):') || '0');
    if (!amount || amount <= 0) return;
    const idKey = generateUUID();
    apiRequest(`/groups/${groupId}/pay`, 'POST', {
        amount, idempotencyKey: idKey
    }, { 'Idempotency-Key': idKey })
        .then(() => {
            showToast('Contribution Successful', `Contributed ${fmtBDT(amount)} to group pool`, 'success');
            loadGroups();
            loadDashboard();
            loadNotifications();
        })
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
            const meta = getTransactionMeta(t);
            return `
            <tr>
                <td><span class="mono-id">${t.transactionNumber || t.id?.slice(0, 12) + '...'}</span></td>
                <td class="font-sm">${fmtDate(meta.dateVal)}</td>
                <td class="font-bold">${meta.counterparty}</td>
                <td class="font-sm text-body">${t.purpose || '—'}</td>
                <td>${statusBadge(t.status)}</td>
                <td class="${meta.amountClass}">${meta.amountSign}${fmtBDT(t.amount)}</td>
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
        const meta = getTransactionMeta(t);

        document.getElementById('modal-txn-body').innerHTML = `
            <div class="review-box mb-16">
                <div class="review-row"><span class="review-key">Transaction #</span><span class="mono-id">${t.transactionNumber || '—'}</span></div>
                <div class="review-row"><span class="review-key">Status</span><span>${statusBadge(t.status)}</span></div>
                <div class="review-row"><span class="review-key">Date</span><span class="review-val">${fmtDate(meta.dateVal)}</span></div>
                <div class="review-row"><span class="review-key">Sender</span><span class="review-val">${t.senderUsername || t.senderAccountNumber || 'System Vault'}</span></div>
                <div class="review-row"><span class="review-key">Recipient</span><span class="review-val">${t.receiverUsername || t.recipientUsername || t.receiverAccountNumber || 'Recipient'}</span></div>
                <div class="review-row"><span class="review-key">Amount</span><span class="review-val ${meta.amountClass}">${meta.amountSign}${fmtBDT(t.amount)}</span></div>
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
        const balance = w.availableBalance ?? w.balance ?? p.balance ?? 0;
        const sent = w.totalSent ?? w.totalDebitedAmount ?? 0;
        const received = w.totalReceived ?? w.totalCreditedAmount ?? 0;
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
                <div class="review-row"><span class="review-key">Balance</span><span class="review-val" style="color:var(--primary);font-size:18px;font-weight:700;">${fmtBDT(balance)}</span></div>
                <div class="review-row"><span class="review-key">Total Sent</span><span class="amt-out">${fmtBDT(sent)}</span></div>
                <div class="review-row"><span class="review-key">Total Received</span><span class="amt-in">${fmtBDT(received)}</span></div>
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

/* ============================================================
   NOTIFICATION CENTER & REAL-TIME TOAST ALERTS
   ============================================================ */
let notificationCount = 0;

function toggleNotifications(e) {
    if (e) e.stopPropagation();
    const drop = document.getElementById('notif-dropdown');
    if (!drop) return;
    const isShowing = drop.classList.contains('show');
    if (!isShowing) {
        drop.classList.add('show');
        loadNotifications();
    } else {
        drop.classList.remove('show');
    }
}

async function loadNotifications() {
    if (!authToken) return;
    try {
        const [reqRes, txRes, grpRes] = await Promise.all([
            apiRequest('/requests/incoming').catch(() => ({ data: [] })),
            apiRequest('/transfers?page=1&pageSize=6').catch(() => ({ data: [] })),
            apiRequest('/groups').catch(() => ({ data: [] }))
        ]);

        const incomingReqs = (reqRes.data || []).filter(r => {
            const st = (r.status || '').toUpperCase();
            return st === 'PENDING' || st === 'PARTIALLYPAID' || st === 'PARTIALLY_PAID';
        });

        const myGroups = (grpRes.data || []).filter(g => {
            const st = (g.status || '').toUpperCase();
            const isActive = st === 'PENDING' || st === 'PARTIALLYPAID' || st === 'PARTIALLY_PAID';
            const myMem = (g.members || []).find(m => m.userId === currentUser?.id);
            // Alert if user is an invited member and hasn't fully paid yet
            return isActive && myMem && (myMem.status || '').toUpperCase() !== 'PAID';
        });

        const recentTxns = txRes.data || [];
        const items = [];

        // Group collection invitations notifications (TOP PRIORITY)
        myGroups.forEach(g => {
            const myMem = (g.members || []).find(m => m.userId === currentUser?.id);
            const reqAmt = myMem ? (myMem.remainingAmount || myMem.requiredAmount) : 0;
            items.push({
                type: 'req',
                icon: '👥',
                title: `Group Pool: "${g.title}"`,
                desc: `Invited by ${g.creatorUsername} • Your assigned share: ${fmtBDT(reqAmt)}`,
                time: g.createdAtUtc || g.createdAt,
                action: () => { toggleNotifications(); navigateTo('groups'); }
            });
        });

        // Incoming requests notifications
        incomingReqs.forEach(r => {
            const name = r.requesterName || r.requesterUsername || r.requesterAccountNumber || 'A peer';
            items.push({
                type: 'req',
                icon: '📥',
                title: `Money Request from ${name}`,
                desc: `Requested ${fmtBDT(r.remainingAmount)} • "${r.note || 'No note'}"`,
                time: r.createdAtUtc || r.createdAt,
                action: () => { toggleNotifications(); navigateTo('requests'); }
            });
        });

        // Recent transaction alerts
        recentTxns.slice(0, 4).forEach(t => {
            const meta = getTransactionMeta(t);
            items.push({
                type: 'send',
                icon: meta.isDebit ? '📤' : '💸',
                title: meta.isDebit ? `Money Sent to ${meta.counterparty}` : `Money Received from ${meta.counterparty}`,
                desc: `${meta.isDebit ? 'Debited' : 'Credited'} ${fmtBDT(t.amount)} • "${t.purpose || 'Direct safe send'}"`,
                time: meta.dateVal,
                action: () => { toggleNotifications(); navigateTo('activity'); }
            });
        });

        // Total active unread action count
        notificationCount = incomingReqs.length + myGroups.length;
        const badge = document.getElementById('notif-badge');
        if (badge) {
            if (notificationCount > 0) {
                badge.textContent = notificationCount;
                badge.style.display = 'flex';
            } else {
                badge.style.display = 'none';
            }
        }

        // Render dropdown list
        const listEl = document.getElementById('notif-list');
        if (listEl) {
            if (!items.length) {
                listEl.innerHTML = `<div class="empty-state" style="padding:24px;">No new alerts 🔔</div>`;
            } else {
                listEl.innerHTML = items.map((item, idx) => `
                    <div class="notif-item" onclick="handleNotifClick(${idx})">
                        <div class="notif-icon ${item.type}">${item.icon}</div>
                        <div class="notif-content">
                            <div class="notif-title">${item.title}</div>
                            <div class="notif-sub">${item.desc}</div>
                            <div class="notif-time">${fmtDate(item.time)}</div>
                        </div>
                    </div>`).join('');
                window._currentNotifItems = items;
            }
        }
    } catch { /* silently handle */ }
}

function handleNotifClick(idx) {
    if (window._currentNotifItems && window._currentNotifItems[idx]) {
        window._currentNotifItems[idx].action();
    }
}

function markAllNotificationsRead() {
    const badge = document.getElementById('notif-badge');
    if (badge) badge.style.display = 'none';
    notificationCount = 0;
    const drop = document.getElementById('notif-dropdown');
    if (drop) drop.classList.remove('show');
    showToast('Notifications Cleared', 'All active alerts marked as acknowledged.', 'info');
}

function showToast(title, message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const icons = {
        success: '✓',
        info:    '🔔',
        warning: '⚠️',
        danger:  '✗'
    };

    const card = document.createElement('div');
    card.className = `toast-card ${type}`;
    card.innerHTML = `
        <div style="font-size:18px;line-height:1;font-weight:bold;color:var(--${type === 'info' ? 'primary' : type});">${icons[type] || '🔔'}</div>
        <div style="flex:1;">
            <div style="font-size:13px;font-weight:700;color:var(--text-heading);">${title}</div>
            <div style="font-size:12px;color:var(--text-body);margin-top:2px;">${message}</div>
        </div>`;

    container.appendChild(card);

    setTimeout(() => {
        card.style.opacity = '0';
        card.style.transform = 'translateY(-10px) scale(0.96)';
        setTimeout(() => card.remove(), 250);
    }, 4500);
}
