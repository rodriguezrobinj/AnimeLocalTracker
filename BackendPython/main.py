from fastapi import FastAPI
from datetime import datetime

app = FastAPI()

@app.get("/ping")
def ping():
    return {
        "status": "ok",
        "message": "AnimeTracker Daemon (Python/FastAPI) is online and ready for AI tasks!",
        "time": datetime.now().isoformat()
    }

if __name__ == "__main__":
    import uvicorn
    print("Starting AnimeTracker Python Daemon on port 8000...")
    uvicorn.run(app, host="0.0.0.0", port=8000)
