# SmartPantry

> AI-powered pantry & recipe assistant — F# / WebSharper / SQLite / Tailwind / Groq

SmartPantry helps you stop throwing food away. Track what's in your pantry,
and let an LLM suggest a quick recipe from the ingredients you actually have.

## Status

🚧 **Under construction** — bootstrapping the project. See `project_plan_basic.md`
for the full implementation roadmap.

## Tech stack

- **Backend & Frontend:** F# (.NET 10) + WebSharper Client-Server
- **Database:** SQLite + Dapper
- **Styling:** Tailwind CSS (build-time, MSBuild integrated)
- **AI:** Groq API (Llama-3.3-70b, JSON mode)
- **DevOps:** Multi-stage Docker, GitHub Actions → ghcr.io

## Quick start (local Docker)

```bash
cp .env.example .env       # then fill in GROQ_API_KEY
docker compose up
```

App will be available at http://localhost:8080.

## License

For educational use (HHC62Y Project Omega).
