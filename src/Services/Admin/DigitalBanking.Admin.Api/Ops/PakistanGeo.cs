namespace DigitalBanking.Admin.Api.Ops;

public sealed record City(string Name, double Lat, double Lng, double Weight);

public sealed record Province(
    string Code,
    string Name,
    double PopulationMillions,
    string CnicPrefix,
    IReadOnlyList<City> Cities,
    IReadOnlyList<string> Surnames);

/// <summary>Reference data for the four regional clusters. Islamabad is served by the Punjab cluster.</summary>
public static class PakistanGeo
{
    public static readonly IReadOnlyList<Province> Provinces =
    [
        new("PB", "Punjab", 127.7, "3",
            [
                new("Lahore", 31.5204, 74.3587, 13.0),
                new("Faisalabad", 31.4504, 73.1350, 3.6),
                new("Islamabad", 33.6844, 73.0479, 1.2),
                new("Rawalpindi", 33.5651, 73.0169, 2.3),
                new("Multan", 30.1575, 71.5249, 2.2),
                new("Gujranwala", 32.1877, 74.1945, 2.0),
                new("Sialkot", 32.4945, 74.5229, 0.9),
                new("Bahawalpur", 29.3956, 71.6836, 0.9),
                new("Sargodha", 32.0836, 72.6711, 0.8)
            ],
            ["Butt", "Chaudhry", "Malik", "Sheikh", "Rana", "Gill", "Bajwa", "Cheema", "Awan", "Qureshi", "Bhatti", "Warraich"]),
        new("SD", "Sindh", 55.7, "4",
            [
                new("Karachi", 24.8607, 67.0011, 20.4),
                new("Hyderabad", 25.3960, 68.3578, 2.4),
                new("Sukkur", 27.7052, 68.8574, 0.6),
                new("Larkana", 27.5570, 68.2264, 0.5),
                new("Nawabshah", 26.2442, 68.4100, 0.3),
                new("Mirpur Khas", 25.5276, 69.0111, 0.3)
            ],
            ["Memon", "Soomro", "Shah", "Abbasi", "Jatoi", "Siddiqui", "Ansari", "Khoso", "Channa", "Mirza", "Rajput", "Pathan"]),
        new("KP", "Khyber Pakhtunkhwa", 40.9, "1",
            [
                new("Peshawar", 34.0151, 71.5249, 2.0),
                new("Mardan", 34.1986, 72.0404, 0.4),
                new("Abbottabad", 34.1688, 73.2215, 0.3),
                new("Mingora", 34.7717, 72.3602, 0.3),
                new("Dera Ismail Khan", 31.8314, 70.9019, 0.25),
                new("Kohat", 33.5869, 71.4429, 0.2)
            ],
            ["Khan", "Afridi", "Yousafzai", "Khattak", "Durrani", "Mohmand", "Swati", "Bangash", "Orakzai", "Shinwari", "Marwat", "Gandapur"]),
        new("BL", "Balochistan", 14.9, "5",
            [
                new("Quetta", 30.1798, 66.9750, 1.6),
                new("Turbat", 26.0031, 63.0544, 0.27),
                new("Khuzdar", 27.8000, 66.6167, 0.2),
                new("Gwadar", 25.1264, 62.3225, 0.14),
                new("Sibi", 29.5430, 67.8773, 0.1),
                new("Chaman", 30.9210, 66.4597, 0.12)
            ],
            ["Baloch", "Bugti", "Marri", "Mengal", "Rind", "Raisani", "Jamali", "Kakar", "Achakzai", "Zehri", "Lehri", "Bizenjo"])
    ];

    public static readonly string[] MaleNames =
    [
        "Muhammad", "Ahmed", "Ali", "Hassan", "Usman", "Bilal", "Hamza", "Fahad", "Imran", "Kashif", "Faisal",
        "Zeeshan", "Asad", "Saad", "Waqas", "Junaid", "Tariq", "Shahid", "Nadeem", "Adnan", "Salman", "Haris",
        "Abdullah", "Umar", "Zain", "Danish", "Rizwan", "Yasir", "Shoaib", "Arslan"
    ];

    public static readonly string[] FemaleNames =
    [
        "Ayesha", "Fatima", "Zainab", "Maryam", "Hira", "Sana", "Amna", "Mahnoor", "Iqra", "Rabia", "Sadia",
        "Noor", "Khadija", "Mehwish", "Anum", "Sidra", "Bushra", "Saima", "Areeba", "Kiran"
    ];

    public static readonly (string Name, double Weight)[] AccountTypes =
    [
        ("Asaan Account", 0.30), ("Savings", 0.25), ("Current", 0.18), ("Islamic Savings", 0.15),
        ("Roshan Digital", 0.07), ("Freelancer", 0.05)
    ];

    public static readonly (string Name, double Weight)[] Channels =
    [
        ("Mobile App", 0.44), ("ATM", 0.18), ("RAAST", 0.12), ("IBFT", 0.08), ("POS", 0.08), ("Branch", 0.05),
        ("Bill Payment", 0.05)
    ];

    public static Province? FindProvince(string name)
    {
        return Provinces.FirstOrDefault(province =>
            string.Equals(province.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(province.Code, name, StringComparison.OrdinalIgnoreCase));
    }

    public static T PickWeighted<T>(IReadOnlyList<T> items, Func<T, double> weight, Random random)
    {
        var total = 0d;
        foreach (var item in items)
        {
            total += weight(item);
        }

        var roll = random.NextDouble() * total;
        foreach (var item in items)
        {
            roll -= weight(item);
            if (roll <= 0)
            {
                return item;
            }
        }

        return items[^1];
    }
}
