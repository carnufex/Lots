# 0016. Knowledge (RAG): Postgres hybrid search, embeddings via the model contract, retrieval as a tool

Status: accepted (2026-10-10). Decides #49; shapes M6.

## Context

Lots needs retrieval over organisation knowledge (runbooks, wiki pages, docs) without giving up its principles: policy outside the
model, identity follows the user, tool output is untrusted, everything traced. The deployment already has Postgres (CloudNativePG in
the homelab, `pgvector` 0.8 available but not installed; installing it needs a database superuser) and an OpenAI-compatible model
endpoint (Ollama, which serves `nomic-embed-text` today).

## Decision

1. **Embeddings go through the model contract.** `IEmbeddingModel` calls `/v1/embeddings` on the model alias `embed`
   (#135) so the same endpoints, keys and fallback apply. Every chunk stores the embedding model name and dimension; a
   change of model re-embeds (an index never mixes models). Default for development: `nomic-embed-text`. For Swedish-heavy corpora
   `bge-m3` is the recommended upgrade; the retrieval evals (#57) are the arbiter, not this ADR.
2. **Storage in the shell's Postgres**, in tables the shell creates itself at startup (idempotent SQL, not EF migrations), so a
   database where the extension cannot be installed still starts:
   - with the `vector` extension: a `vector` column with an HNSW index (cosine);
   - without it: `real[]` and similarity computed in SQL, logged as a warning and shown on the Knowledge page. Fine up to tens of
     thousands of chunks; install the extension (`CREATE EXTENSION vector`, CNPG `Database` resource) beyond that.
3. **Hybrid search**: Postgres full-text (`simple` configuration, so Swedish and English both work without stemming surprises) plus
   vector similarity, fused with reciprocal-rank fusion (k = 60). A reranker is an optional later step behind the same interface.
4. **Retrieval is a tool.** `search_knowledge` is offered by a built-in tool source and must be declared in a profile with a risk
   class like any other tool, so policy, approvals, audit and trace apply unchanged (principles 1, 2, 5). Results carry source,
   title, URL, date and a stable chunk id; the model is told to cite them; the UI shows the sources.
5. **Access control at query time**: every source has readers (roles, IdP groups mapped to roles, or user ids); the search filters
   by the run's principal inside the query, so a user can never retrieve what they may not read (principle 3). Personal sources
   (#56) are sources whose only reader is their owner.
6. **Retrieved text is untrusted** (principle 4): returned wrapped and labelled as data; it can never change instructions,
   permissions or approvals.
7. **Ingestion is a job** (principle 8): fetch -> extract (headings kept) -> heading-aware chunking with overlap -> embed -> store with
   content hash. Unchanged documents are not re-embedded; deleted documents and sources disappear from search. Jobs are leased like
   runs, so they survive restarts and replicas.

## Consequences

- No new infrastructure; pgvector is an optimisation the operator can turn on.
- Knowledge tables live outside the EF model; their schema version is tracked in a small `knowledge_schema` table.
- Conflict detection and voting (#48) build on stable chunk ids and content hashes.
