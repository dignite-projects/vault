---
description: "ABP background job unit of work boundaries and long-running work rules"
paths:
  - "modules/vault.extract/src/**/*BackgroundJob*.cs"
  - "modules/vault.extract/src/**/*Job.cs"
  - "modules/vault.extract/src/**/*JobArgs.cs"
---

# Background jobs

## Short units of work

A job that does slow or external work (blob IO, OCR, LLM calls, HTTP, long CPU work) runs in three phases, never inside one long `[UnitOfWork]`:

1. **Begin**: load the aggregates, mark the run as running, commit.
2. **External**: do the slow work with no ambient UoW.
3. **Complete**: reload, apply the outcome, commit.

A long ambient UoW holds connections and locks while the external work runs. That blocks ordinary HTTP reads and causes SQL timeouts even on primary-key queries. Tests assert `_unitOfWorkManager.Current.ShouldBeNull()` at the external call.

If a job's args carry a run id, persist that id before enqueueing, and use it to complete or fail the run.

## Pipeline runs

`DocumentPipelineRun` is its own aggregate root. In a job, load the document with `DocumentRepository.GetAsync(id, includeDetails: false)`, load the run with `RunRepository.FindAsync(runId)`, and change its state through `DocumentPipelineRunManager`. The shared prelude and failure handling are in `DocumentPipelineBackgroundJobBase<TArgs>` (`LoadDocumentAndRunAsync` / `FailRunAsync`).

## When a job ends instead of rethrowing

A rethrow hands the job to ABP's retry loop, which is right for throttling, timeouts and server errors. Two failures never succeed on retry, so the job ends:

- **The document is gone.** `GetAsync` throws `EntityNotFoundException` for a deleted document. Catch exactly that, around the Begin phase only (`DocumentPipelineBackgroundJobBase.IsDocumentGone`), call `EndForDeletedDocumentAsync`, and return. Retrying left a restored document with a `Pending` run that nothing executed.
- **The provider refused the request itself.** After `FailRunAsync`, ask `EndOnPermanentProviderFailure` and return when it says so. `ProviderFailureClassifier` treats an HTTP 4xx other than 408 / 429 as permanent, and also a `System.IO.InvalidDataException` anywhere in the exception chain (an input that is invalid or over a configured limit). A text-extraction provider that rejects an input for a permanent reason throws that type, not `InvalidOperationException`. A failure with no HTTP status stays retryable.

`DocumentSegmentationJob` has no pipeline run of its own. On a permanent refusal it degrades through `IncompleteOrSkipAsync` and returns.
