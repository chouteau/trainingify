window.VelodromifyBLE = {
    device: null,
    server: null,
    characteristic: null,
    dotnetRef: null,

    init: function (dotnetRef) {
        this.dotnetRef = dotnetRef;
    },

    connect: async function () {
        try {
            console.log('Requesting Bluetooth Device...');
            this.device = await navigator.bluetooth.requestDevice({
                filters: [
                    { services: ['fitness_machine'] }, // FTMS
                    { services: ['cycling_speed_and_cadence'] }, // CSC
                    { services: ['heart_rate'] } // HRM
                ],
                optionalServices: [
                    'fitness_machine',
                    'cycling_speed_and_cadence',
                    'heart_rate'
                ]
            });

            this.device.addEventListener('gattserverdisconnected', this.onDisconnected);

            console.log('Connecting to GATT Server...');
            this.server = await this.device.gatt.connect();

            console.log('Getting Services...');
            // Try FTMS first
            try {
                const service = await this.server.getPrimaryService('fitness_machine');
                const characteristic = await service.getCharacteristic('indoor_bike_data');
                await characteristic.startNotifications();
                characteristic.addEventListener('characteristicvaluechanged', this.handleFTMSData.bind(this));
                console.log('FTMS Connected');
                return true;
            } catch (e) {
                console.log('FTMS not found, trying CSC...');
            }

            // Try CSC
            try {
                const service = await this.server.getPrimaryService('cycling_speed_and_cadence');
                const characteristic = await service.getCharacteristic('csc_measurement');
                await characteristic.startNotifications();
                characteristic.addEventListener('characteristicvaluechanged', this.handleCSCData.bind(this));
                console.log('CSC Connected');
                return true;
            } catch (e) {
                console.log('CSC not found');
            }
            
            return false;

        } catch (error) {
            console.log('Argh! ' + error);
            return false;
        }
    },

    handleFTMSData: function (event) {
        const value = event.target.value;
        // Parse FTMS Indoor Bike Data
        // Flags: 2 bytes
        const flags = value.getUint16(0, true);
        let offset = 2;

        // Bit 0: More Data (ignored for now)
        // Bit 1: Average Speed present (if 0, Instant Speed is present)
        // Bit 2: Instant Cadence present
        // Bit 3: Average Cadence present
        // Bit 4: Total Distance present
        // ...
        // Bit 6: Instant Power present
        
        // Speed (Km/h) - Uint16, 0.01 resolution
        const speed = value.getUint16(offset, true) * 0.01;
        offset += 2;
        
        // If Cadence present (Bit 2)
        let cadence = 0;
        if (flags & (1 << 2)) {
            cadence = value.getUint16(offset, true) * 0.5;
            offset += 2;
        }

        // If Power present (Bit 6)
        // Need to check other bits to calculate offset correctly if they are present
        // This is a simplified parser. A robust one needs to check all flag bits.
        // Let's assume standard order: Speed, Cadence, Distance, Resistance, Power, HeartRate
        
        // For now, let's just send what we have.
        // Ideally we should implement full parsing logic.
        
        // Let's try to find Power.
        // Distance (Bit 4) - Uint24
        if (flags & (1 << 4)) {
            offset += 3;
        }
        // Resistance (Bit 5) - Int16
        if (flags & (1 << 5)) {
            offset += 2;
        }
        
        let power = 0;
        if (flags & (1 << 6)) {
            power = value.getInt16(offset, true);
        }

        this.dotnetRef.invokeMethodAsync('UpdateRideData', speed, power, cadence);
    },

    handleCSCData: function (event) {
        // CSC Measurement
        // Flags: 1 byte
        // Bit 0: Wheel Revolution Data Present
        // Bit 1: Crank Revolution Data Present
        
        // This requires state tracking (previous timestamp/revolutions) to calculate speed/cadence.
        // For simplicity in this MVP, we might skip full CSC implementation or do a basic one.
        // Let's just log it for now.
        console.log('CSC Data received (not implemented fully)');
    },

    onDisconnected: function (event) {
        console.log('Device disconnected');
    }
};
