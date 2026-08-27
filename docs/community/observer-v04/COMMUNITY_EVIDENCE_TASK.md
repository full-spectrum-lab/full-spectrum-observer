# Observer v0.4 Community Evidence Task

[English](COMMUNITY_EVIDENCE_TASK.md) · [简体中文](COMMUNITY_EVIDENCE_TASK.zh-CN.md) · [中文参与者操作手册](PARTICIPANT_RUNBOOK.zh-CN.md)

**Status:** `DOCUMENTED / RECRUITMENT_PREPARATION / EXECUTION_NOT_OPEN`

**R&D relationship:** `NON_BLOCKING_FOR_R&D`

**Release relationship:** this task does not authorize a Release, deployment, or production claim

## Purpose

This is an ongoing community evidence task for a future, tightly bounded Observer v0.4 pilot. It is designed to collect honest evidence about whether an independent human can:

1. review the redaction of an authorized narrow sample;
2. run an explicitly identified Observer candidate when distribution is separately authorized;
3. understand candidate-only output, evidence references, human-review requirements and `UNKNOWN`;
4. report `ACCEPT`, `CONDITIONAL`, `REJECT` or `UNKNOWN` without pressure to produce a positive result;
5. complete and verify the agreed deletion or exit procedure.

Failures, disagreements and `UNKNOWN` are valid evidence. This task is not a product endorsement campaign.

## Current public boundary

- The current public release truth remains the table in the repository root README.
- No public Observer v0.4 package is offered by this task today.
- The execution phase stays closed until the Owner separately authorizes an exact candidate, checksum, task scope and distribution method.
- RP-001 private source material, Owner private keys and unpublished governance files are not community assets and must not be requested or shared.
- Publishing this task does not change Observer v0.4, v0.4.5, v0.5 or v0.6 requirements.

## Who may participate

One person may act as both the independent human redaction reviewer and the target user only when that person:

- did not implement the tested Observer candidate;
- did not make the data-authorization decision;
- is authorized to inspect both the source and redacted copy locally;
- discloses the dual role;
- actually operates the candidate and writes an independent result.

If a participant cannot lawfully inspect the source, they may act only as a target user. If a participant uses their own material and also performs every role, the result is classified as `COMMUNITY_SELF_PILOT`, not independent external acceptance.

## Evidence expected

The future execution package will require:

- an authorized-sample manifest reference;
- an independent redaction report without raw sensitive text;
- a target-user acceptance record;
- a deletion/exit record;
- exact hashes for the candidate, Pack, input and permitted logs;
- role-overlap and conflict-of-interest disclosure;
- all failures, deviations and `UNKNOWN` states.

Do not submit raw source material, reversible redactions, private keys, credentials, cookies, personal paths or unapproved screenshots.

## Express interest

Until execution opens, a prospective participant may open a GitHub Issue titled:

```text
[Community Evidence Interest] Observer v0.4 - <short participant label>
```

The Issue should contain only:

- country/region and time zone;
- whether the participant is independent of the implementation team;
- whether they can provide their own lawfully controlled sample;
- whether they are willing to perform redaction review, target-user testing, or both;
- operating-system availability;
- no source data and no personal identifiers beyond what the participant chooses to make public.

Opening an Issue does not grant access to an internal candidate or private data. Distribution and execution require a separate Owner decision.

## Decision boundary

Community evidence does not block later R&D branches. It also does not silently upgrade the public release status. Each accepted evidence package supports only its exact participant, sample, version and time boundary.

The detailed Chinese participant procedure is in [PARTICIPANT_RUNBOOK.zh-CN.md](PARTICIPANT_RUNBOOK.zh-CN.md).
