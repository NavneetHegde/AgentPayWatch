# Project Name: **AgentPay Watch**

> **Tagline:** *Agents watch. Humans approve. Payments happen.*

---

## Overview
**AgentPay Watch** is a **hackathon-ready, agent-driven payment platform**.

Users define **what they want to buy** and **how much they’re willing to pay**. Autonomous agents continuously monitor product availability. When a match is found, a **Google A2P (verified) message** asks the user for approval. Upon confirmation, a **payment agent executes the transaction automatically**.

The solution is intentionally **simple, viable, and demo-friendly**, while showcasing:
- Agent autonomy
- Human-in-the-loop trust
- Real-world A2P compliance
- Agent-executed payments on Azure

---

## Core Capabilities
- Product search & criteria definition
- Autonomous product monitoring agent
- Event-driven workflow
- Google A2P approval (human-in-the-loop)
- Agent-based payment execution
- Clear audit trail

---

## High-Level Architecture

```
User
 ↓
Blazor Web UI
 ↓
.NET Minimal API
 ↓
Cosmos DB (Criteria + State)
 ↓
Product Watch Agent
 ↓
Azure Service Bus (Events)
 ↓
Approval Agent (Google A2P)
 ↓
User Approval
 ↓
Payment Execution Agent
 ↓
Payment Provider
```

---

## Technology Stack (Simple & Viable)

| Layer | Technology |
|-----|-----------|
| Local Orchestration | Aspire |
| Frontend | Blazor WebAssembly |
| APIs | .NET 8 Minimal API |
| Agents | Microsoft Agent Framework (.NET) |
| AI Reasoning | Azure Foundry |
| Messaging | Google A2P (RCS / Verified SMS) |
| Events | Azure Service Bus |
| Data | Azure Cosmos DB |
| Hosting | Azure Container Apps |

---

## User Experience (UI Design)

### 1. Dashboard
- Active Watches
- Pending Approvals
- Completed Purchases

Primary CTA: **Create New Watch**

---

### 2. Create Product Watch
User defines intent:
- Product name
- Max price
- Preferred seller
- Approval mode (default: ask before buying)
- Payment method
- Google A2P notifications

Simple form, no advanced options by default.

---

### 3. Active Watch View
Shows agent autonomy:
- Monitoring status
- Last checked time
- Criteria summary

---

### 4. Pending Approval View
Trust-focused screen:
- Product details
- Price & seller
- Approval timeout
- Status synced with Google A2P

---

### 5. Purchase Confirmation
- Payment success
- Order ID
- Receipt link

---

## Core Agents

### 1. Product Watch Agent
**Type:** Autonomous Agent

**Responsibilities:**
- Periodically query product sources (mocked)
- Normalize product data
- Match against stored criteria
- Emit `PRODUCT_MATCH_FOUND` event

---

### 2. Approval Agent (Google A2P)
**Type:** A2P + Human-in-the-Loop Agent

**Responsibilities:**
- Listen for product match events
- Send approval request via Google A2P
- Receive user response (BUY / SKIP)
- Emit approval decision event

---

### 3. Payment Execution Agent
**Type:** Transactional Agent

**Responsibilities:**
- Revalidate price & availability
- Run lightweight risk check (Azure Foundry)
- Execute payment
- Persist transaction
- Send confirmation via A2P

---

## Event Flow

### Product Match Event
```json
{
  "eventType": "PRODUCT_MATCH_FOUND",
  "product": "iPhone 15 Pro",
  "price": 950,
  "seller": "Amazon",
  "userId": "U123"
}
```

### Approval Event
```json
{
  "eventType": "PAYMENT_APPROVED",
  "source": "GOOGLE_A2P",
  "userId": "U123"
}
```

---

## .NET Minimal API Design

### Save Product Criteria
```http
POST /criteria
```
Stores user-defined watch criteria.

### Google A2P Callback
```http
POST /a2p/callback
```
Receives BUY / SKIP response.

### Execute Payment
```http
POST /payments/execute
```
Invoked internally after approval.

---

## Agent-Based Payment Protocol

```json
{
  "intent": "EXECUTE_PAYMENT",
  "amount": 950,
  "currency": "USD",
  "merchant": "Amazon",
  "approved": true
}
```

Ensures consistent, auditable agent communication.

---

## Google A2P Approval Flow

1. Approval agent sends verified A2P message
2. User replies `BUY` or `SKIP`
3. Callback validates response
4. Approved payments proceed automatically

---

## Google A2P Compliance & Verification

### Sender Verification
- Google-verified sender (RCS or SMS)
- Registered business identity and use case

### User Consent
- Explicit opt-in during onboarding
- Opt-out supported via `STOP`

### Allowed Message Types
- Product availability alerts
- Payment approval requests
- Payment confirmations only

### Message Rules
- Clear business identity
- No links or sensitive data
- Single call-to-action

### Security
- No card or payment data in messages
- Approval tokens instead of details
- Callback signature validation

### Audit
- All messages logged with payment intent

---

## Azure Foundry Deployment

| Component | Azure Service |
|--------|---------------|
| Minimal APIs | Azure App Service |
| Agents | Azure Container Apps |
| AI Models | Azure Foundry |
| Events | Azure Service Bus |
| Secrets | Azure Key Vault |

---

## Hackathon Demo Flow (5 Minutes)

1. User creates a product watch
2. Product match event is triggered
3. Google A2P approval message sent
4. User replies BUY
5. Payment agent executes transaction
6. Confirmation received

---

## Why This Wins a Hackathon

- Clear agent autonomy
- Human trust via approval
- Real A2P compliance
- Simple, end-to-end demo
- Production-aligned architecture

> **AgentPay Watch** demonstrates how agents can safely move money — with humans always in control.

