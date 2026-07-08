using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

class Program
{
	// UUIDs for the FTMS service and characteristics
	private static readonly Guid FtmsServiceUuid = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
	private static readonly Guid IndoorBikeDataUuid = Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb");
	private static readonly Guid ControlPointUuid = Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb");

	public static async Task Main(string[] args)
	{
		Console.WriteLine("Recherche du Home Trainer Wahoo...");
		var watcher = new BluetoothLEAdvertisementWatcher
		{
			ScanningMode = BluetoothLEScanningMode.Active
		};
		var tcsDevice = new TaskCompletionSource<BluetoothLEDevice>();
		watcher.Received += async (sender, adv) =>
		{
			string localName = adv.Advertisement.LocalName;
			if (!string.IsNullOrEmpty(localName) && localName.Contains("KICKR", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine($"Appareil trouvé : {localName} (addr {adv.BluetoothAddress:X})");
				try
				{
					var device = await BluetoothLEDevice.FromBluetoothAddressAsync(adv.BluetoothAddress);
					tcsDevice.TrySetResult(device);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Erreur de connexion : {ex.Message}");
				}
				finally
				{
					watcher.Stop();
				}
			}
		};
		watcher.Start();
		BluetoothLEDevice kickr = await tcsDevice.Task;

		if (kickr == null)
		{
			Console.WriteLine("Aucun appareil KICKR trouvé.");
			return;
		}

		Console.WriteLine($"Connexion à {kickr.Name}...");
		var servicesResult = await kickr.GetGattServicesAsync(BluetoothCacheMode.Uncached);
		if (servicesResult.Status != GattCommunicationStatus.Success)
		{
			Console.WriteLine("Impossible d'obtenir les services GATT.");
			return;
		}
		var ftmsService = servicesResult.Services.FirstOrDefault(s => s.Uuid == FtmsServiceUuid);
		if (ftmsService == null)
		{
			Console.WriteLine("Le service FTMS n'est pas disponible.");
			return;
		}

		// Obtenir la caractéristique Indoor Bike Data
		var charResult = await ftmsService.GetCharacteristicsForUuidAsync(IndoorBikeDataUuid);
		if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
		{
			Console.WriteLine("Impossible de trouver la caractéristique Indoor Bike Data.");
			return;
		}

		foreach (var characteristic in charResult.Characteristics)
		{
			Console.WriteLine($"Caractéristique trouvée : {characteristic.Uuid}");
		}

		var indoorBikeData = charResult.Characteristics[0];
		// S'abonner aux notifications pour obtenir cadence et puissance
		indoorBikeData.ValueChanged += IndoorBikeData_ValueChanged;
		var status = await indoorBikeData.WriteClientCharacteristicConfigurationDescriptorAsync(
			GattClientCharacteristicConfigurationDescriptorValue.Notify);
		if (status != GattCommunicationStatus.Success)
		{
			Console.WriteLine("Échec de l'abonnement aux notifications.");
			return;
		}

		// Obtenir la caractéristique Control Point
		charResult = await ftmsService.GetCharacteristicsForUuidAsync(ControlPointUuid);
		if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
		{
			Console.WriteLine("Impossible de trouver la caractéristique Control Point.");
			return;
		}

		var controlPoint = charResult.Characteristics[0];
		// S'abonner aux indications pour recevoir les réponses
		controlPoint.ValueChanged += ControlPoint_ValueChanged;
		status = await controlPoint.WriteClientCharacteristicConfigurationDescriptorAsync(
			GattClientCharacteristicConfigurationDescriptorValue.Indicate);

		if (status != GattCommunicationStatus.Success)
		{
			Console.WriteLine("Échec de l'abonnement aux indications du Control Point.");
			return;
		}

		// Envoyer l'opcode Acquire Control (0x00) suivi d'un identifiant de requête (0x01)
		// Console.WriteLine("Acquisition du contrôle...");
		// await WriteControlPointAsync(controlPoint, new byte[] { 0x00, 0x01 });

		// Attendre la confirmation
		await Task.Delay(1000);

		// Définir une puissance cible en watts. Exemple : 200 W (0xC8 0x00 en little endian)
		Console.WriteLine("Définir puissance cible à 200 W...");
		byte[] powerPayload = new byte[] { 0x05, 0xC8, 0x00, 0x02 }; // opcode 0x05, 200 W, request ID 0x02
																	 // await WriteControlPointAsync(controlPoint, powerPayload);

		Console.WriteLine("Appuyez sur Entrée pour quitter...");
		Console.ReadLine();

		// Nettoyer
		indoorBikeData.ValueChanged -= IndoorBikeData_ValueChanged;
		controlPoint.ValueChanged -= ControlPoint_ValueChanged;
		ftmsService.Dispose();
		kickr.Dispose();
	}

	// Méthode utilitaire pour écrire sur le Control Point
	static async Task WriteControlPointAsync(GattCharacteristic controlPoint, byte[] data)
	{
		var buffer = data.AsBuffer();
		var status = await controlPoint.WriteValueAsync(buffer, GattWriteOption.WriteWithResponse);
		if (status != GattCommunicationStatus.Success)
		{
			Console.WriteLine($"Échec de l'écriture sur le Control Point : {status}");
		}
	}

	// Gestionnaire pour les notifications Indoor Bike Data. Décodage minimal des drapeaux
	static void IndoorBikeData_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
	{
		if (args.CharacteristicValue.Length < 2) return;

		var reader = DataReader.FromBuffer(args.CharacteristicValue);
		reader.ByteOrder = ByteOrder.LittleEndian;
		ushort flags = reader.ReadUInt16();

		// Bit 0: More Data (0 = Instantaneous Speed present)
		double speed = 0;
		if ((flags & (1 << 0)) == 0)
		{
			if (reader.UnconsumedBufferLength >= 2)
			{
				speed = reader.ReadUInt16() * 0.01;
			}
		}

		// Bit 1: Average Speed present (uint16)
		if ((flags & (1 << 1)) != 0)
		{
			if (reader.UnconsumedBufferLength >= 2) reader.ReadUInt16();
		}

		// Bit 2: Instantaneous Cadence present (uint16, 0.5 rpm)
		double cadence = 0;
		if ((flags & (1 << 2)) != 0)
		{
			if (reader.UnconsumedBufferLength >= 2)
			{
				cadence = reader.ReadUInt16() * 0.5;
				Console.Write($"Cadence : {cadence} rpm ");
			}
		}

		// Bit 3: Average Cadence present (uint16)
		if ((flags & (1 << 3)) != 0)
		{
			if (reader.UnconsumedBufferLength >= 2) reader.ReadUInt16();
		}

		// Bit 4: Total Distance present (uint24)
		if ((flags & (1 << 4)) != 0)
		{
			if (reader.UnconsumedBufferLength >= 3)
			{
				reader.ReadByte();
				reader.ReadByte();
				reader.ReadByte();
			}
		}

		// Bit 5: Resistance Level present (sint16)
		if ((flags & (1 << 5)) != 0)
		{
			if (reader.UnconsumedBufferLength >= 2) reader.ReadInt16();
		}

		// Bit 6: Instantaneous Power present (sint16, 1 W)
		short power = 0;
		if ((flags & (1 << 6)) != 0)
		{
			if (reader.UnconsumedBufferLength >= 2)
			{
				power = reader.ReadInt16();
				Console.Write($"Puissance : {power} W ");
			}
		}

		Console.WriteLine();
	}

	// Gestionnaire pour les indications de la réponse Control Point
	static void ControlPoint_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
	{
		var data = new byte[args.CharacteristicValue.Length];
		DataReader.FromBuffer(args.CharacteristicValue).ReadBytes(data);
		Console.WriteLine("Réponse Control Point reçue : " + BitConverter.ToString(data));
	}
}
