package main

import (
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"time"
)

type PingResponse struct {
	Status  string `json:"status"`
	Message string `json:"message"`
	Time    string `json:"time"`
}

func pingHandler(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	response := PingResponse{
		Status:  "ok",
		Message: "AnimeTracker Daemon (Go) is alive and kicking!",
		Time:    time.Now().Format(time.RFC3339),
	}
	json.NewEncoder(w).Encode(response)
}

func main() {
	http.HandleFunc("/ping", pingHandler)

	port := ":8080"
	fmt.Printf("Starting AnimeTracker Daemon on port %s...\n", port)
	
	if err := http.ListenAndServe(port, nil); err != nil {
		log.Fatalf("Server failed to start: %v", err)
	}
}
