(() => {
    const databaseName = "mold-db";
    const databaseVersion = 3;
    const storeName = "games";

    const openDatabase = () => new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, databaseVersion);

        request.onupgradeneeded = () => {
            const database = request.result;
            if (!database.objectStoreNames.contains(storeName)) {
                database.createObjectStore(storeName);
            }
        };

        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });

    const execute = async (mode, operation) => {
        const database = await openDatabase();
        try {
            return await new Promise((resolve, reject) => {
                const transaction = database.transaction(storeName, mode);
                const store = transaction.objectStore(storeName);
                const request = operation(store);

                request.onsuccess = () => resolve(request.result ?? null);
                request.onerror = () => reject(request.error);
                transaction.onabort = () => reject(transaction.error);
            });
        } finally {
            database.close();
        }
    };

    window.moldStorage = {
        get: key => execute("readonly", store => store.get(key)),
        set: (key, value) => execute("readwrite", store => store.put(value, key))
    };
})();