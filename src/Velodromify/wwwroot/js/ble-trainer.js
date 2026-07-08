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
        if (value.byteLength < 2) return;

        const flags = value.getUint16(0, true);
        let offset = 2;

        // Bit 0: More Data (0 = Instantaneous Speed present)
        let speed = 0;
        if ((flags & (1 << 0)) === 0) {
            if (offset + 2 <= value.byteLength) {
                speed = value.getUint16(offset, true) * 0.01;
                offset += 2;
            }
        }

        // Bit 1: Average Speed present (uint16)
        if (flags & (1 << 1)) {
            offset += 2;
        }

        // Bit 2: Instantaneous Cadence present (uint16)
        let cadence = 0;
        if (flags & (1 << 2)) {
            if (offset + 2 <= value.byteLength) {
                cadence = value.getUint16(offset, true) * 0.5;
                offset += 2;
            }
        }

        // Bit 3: Average Cadence present (uint16)
        if (flags & (1 << 3)) {
            offset += 2;
        }

        // Bit 4: Total Distance present (uint24)
        if (flags & (1 << 4)) {
            offset += 3;
        }

        // Bit 5: Resistance Level present (sint16)
        if (flags & (1 << 5)) {
            offset += 2;
        }

        // Bit 6: Instantaneous Power present (sint16)
        let power = 0;
        if (flags & (1 << 6)) {
            if (offset + 2 <= value.byteLength) {
                power = value.getInt16(offset, true);
                offset += 2;
            }
        }

        // Bit 7: Average Power present (sint16)
        if (flags & (1 << 7)) {
            offset += 2;
        }

        // Bit 8: Expended Energy present (Total: uint16, Per Hour: uint16, Per Minute: uint8)
        if (flags & (1 << 8)) {
            offset += 5;
        }

        // Bit 9: Heart Rate present (uint8)
        if (flags & (1 << 9)) {
            offset += 1;
        }

        // Bit 10: Metabolic Equivalent present (uint8)
        if (flags & (1 << 10)) {
            offset += 1;
        }

        // Bit 11: Elapsed Time present (uint16)
        if (flags & (1 << 11)) {
            offset += 2;
        }

        // Bit 12: Remaining Time present (uint16)
        if (flags & (1 << 12)) {
            offset += 2;
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
